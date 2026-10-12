using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Audit;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests;

/// <summary>
/// OBS-4 (ADR-008): the tenant audit log records semantic events scoped to the tenant, is append-only
/// (no update/delete from application code), and is purged on tenant dissolve via the contributor's
/// set-based delete (which bypasses the append-only guard). No secrets — metadata is caller-supplied JSON.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AuditLogTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task RecordAsync_StampsTenant_AndStoresFields()
    {
        var tenant = Guid.CreateVersion7();
        var actor = Guid.CreateVersion7();

        await using (var db = Fixture.CreateContext(tenant))
        {
            await new AuditLog(new EfRepository<AuditEvent>(db), TimeProvider.System)
                .RecordAsync("member.role_changed", actor, "TenantMembership", "m1", new { from = "member", to = "owner" });
            await db.SaveChangesAsync();
        }

        await using var read = Fixture.CreateContext(tenant);
        var e = await read.Set<AuditEvent>().SingleAsync();
        Assert.Equal(tenant, e.TenantId);
        Assert.Equal(actor, e.ActorUserId);
        Assert.Equal("member.role_changed", e.Action);
        Assert.Equal("TenantMembership", e.EntityType);
        Assert.Contains("owner", e.Metadata); // serialized JSON metadata
    }

    [Fact]
    public async Task Events_AreScopedToTheirTenant()
    {
        var tenantA = Guid.CreateVersion7();
        await RecordAsync(tenantA, "a.event");

        await using (var readB = Fixture.CreateContext(Guid.CreateVersion7()))
            Assert.Empty(await readB.Set<AuditEvent>().ToListAsync()); // another tenant sees nothing

        await using var readA = Fixture.CreateContext(tenantA);
        Assert.Single(await readA.Set<AuditEvent>().ToListAsync());
    }

    [Fact]
    public async Task AuditEvent_IsAppendOnly_UpdateThrows()
    {
        var tenant = Guid.CreateVersion7();
        await RecordAsync(tenant, "x.event");

        await using var db = Fixture.CreateContext(tenant);
        var e = await db.Set<AuditEvent>().SingleAsync();
        e.Action = "tampered";

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task AuditEvent_IsAppendOnly_DeleteThrows()
    {
        var tenant = Guid.CreateVersion7();
        await RecordAsync(tenant, "x.event");

        await using var db = Fixture.CreateContext(tenant);
        db.Remove(await db.Set<AuditEvent>().SingleAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Contributor_ReportsAndWipes_OnDissolve()
    {
        var tenant = Guid.CreateVersion7();
        await RecordAsync(tenant, "x.event");

        // Wipe runs inside the dissolve's EnterTenant(target) scope (RLS-2/T6), so the filter scopes the
        // set-based delete to the target — the context is that tenant here (HasData reads cross-tenant).
        await using var db = Fixture.CreateContext(tenant);
        var contributor = new AuditDataContributor(new EfRepository<AuditEvent>(db));
        Assert.True(await contributor.HasDataAsync(tenant));

        await contributor.WipeAsync(tenant); // set-based delete bypasses the append-only guard

        Assert.False(await contributor.HasDataAsync(tenant));
    }

    private async Task RecordAsync(Guid tenant, string action)
    {
        await using var db = Fixture.CreateContext(tenant);
        await new AuditLog(new EfRepository<AuditEvent>(db), TimeProvider.System).RecordAsync(action);
        await db.SaveChangesAsync();
    }
}
