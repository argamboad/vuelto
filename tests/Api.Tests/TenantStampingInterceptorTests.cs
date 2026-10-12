using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Tests.Infrastructure;

namespace Vuelto.Api.Tests;

/// <summary>
/// Proves tenant isolation is structural on the WRITE side too: the stamping interceptor
/// stamps the current tenant onto a new <see cref="ITenantScoped"/> row that left
/// <c>TenantId</c> unset, and fails closed (throws, persists nothing) if a row carries a
/// TenantId belonging to a different tenant. Together with the read-side global query
/// filter this closes the "a feature can silently persist under the wrong tenant" hazard
/// (CONF-1).
/// </summary>
[Collection(PostgresCollection.Name)]
public class TenantStampingInterceptorTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task Insert_WithoutTenantId_StampsCurrentTenant()
    {
        var tenant = Guid.CreateVersion7();

        await using (var db = Fixture.CreateContext(tenant))
        {
            // TenantId deliberately left default — the interceptor must stamp it.
            db.Set<TestWidget>().Add(new TestWidget { Name = "no tenant set" });
            await db.SaveChangesAsync();
        }

        await using var read = Fixture.CreateContext();
        var note = await read.Set<TestWidget>().IgnoreQueryFilters().SingleAsync();
        Assert.Equal(tenant, note.TenantId);
    }

    [Fact]
    public async Task Insert_WithForeignTenantId_Throws_AndPersistsNothing()
    {
        var current = Guid.CreateVersion7();
        var foreignTenant = Guid.CreateVersion7();

        await using (var db = Fixture.CreateContext(current))
        {
            db.Set<TestWidget>().Add(new TestWidget { Name = "wrong tenant", TenantId = foreignTenant });
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using var read = Fixture.CreateContext();
        Assert.Empty(await read.Set<TestWidget>().IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Insert_WithMatchingTenantId_IsAllowed()
    {
        var tenant = Guid.CreateVersion7();

        await using (var db = Fixture.CreateContext(tenant))
        {
            db.Set<TestWidget>().Add(new TestWidget { Name = "explicit but correct", TenantId = tenant });
            await db.SaveChangesAsync();
        }

        await using var read = Fixture.CreateContext();
        Assert.Equal(tenant, (await read.Set<TestWidget>().IgnoreQueryFilters().SingleAsync()).TenantId);
    }

    // v2 audit ADV-1: the write guard must cover UPDATE and DELETE, not just INSERT. A row loaded
    // cross-tenant via the escape hatch must not be mutable/deletable under the wrong current tenant.

    [Fact]
    public async Task Update_ForeignRowLoadedViaHatch_Throws_AndPersistsNothing()
    {
        var (owner, attacker, noteId) = await SeedOwnedNoteAsync();

        await using (var db = Fixture.CreateContext(attacker))
        {
            var foreign = await db.Set<TestWidget>().IgnoreQueryFilters().SingleAsync(n => n.Id == noteId);
            foreign.Name = "hijacked";
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using var read = Fixture.CreateContext();
        var note = await read.Set<TestWidget>().IgnoreQueryFilters().SingleAsync();
        Assert.Equal("owned by A", note.Name);   // unchanged
        Assert.Equal(owner, note.TenantId);
    }

    [Fact]
    public async Task Delete_ForeignRowLoadedViaHatch_Throws_AndPersistsNothing()
    {
        var (_, attacker, noteId) = await SeedOwnedNoteAsync();

        await using (var db = Fixture.CreateContext(attacker))
        {
            var foreign = await db.Set<TestWidget>().IgnoreQueryFilters().SingleAsync(n => n.Id == noteId);
            db.Set<TestWidget>().Remove(foreign);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using var read = Fixture.CreateContext();
        Assert.Single(await read.Set<TestWidget>().IgnoreQueryFilters().ToListAsync()); // still there
    }

    [Fact]
    public async Task Update_OwnRow_IsAllowed()
    {
        var tenant = Guid.CreateVersion7();
        Guid noteId;
        await using (var db = Fixture.CreateContext(tenant))
        {
            var note = new TestWidget { Name = "mine" };
            db.Set<TestWidget>().Add(note);
            await db.SaveChangesAsync();
            noteId = note.Id;
        }

        await using (var db = Fixture.CreateContext(tenant))
        {
            var note = await db.Set<TestWidget>().SingleAsync(n => n.Id == noteId);
            note.Name = "renamed";
            await db.SaveChangesAsync();
        }

        await using var read = Fixture.CreateContext();
        Assert.Equal("renamed", (await read.Set<TestWidget>().IgnoreQueryFilters().SingleAsync()).Name);
    }

    /// <summary>Seeds a note owned by tenant A; returns (owner, a fresh attacker tenant, note id).</summary>
    private async Task<(Guid Owner, Guid Attacker, Guid NoteId)> SeedOwnedNoteAsync()
    {
        var owner = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(owner);
        var note = new TestWidget { Name = "owned by A" };
        db.Set<TestWidget>().Add(note);
        await db.SaveChangesAsync();
        return (owner, Guid.CreateVersion7(), note.Id);
    }
}
