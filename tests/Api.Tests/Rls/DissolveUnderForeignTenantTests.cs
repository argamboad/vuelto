using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Rls;

/// <summary>
/// Hardening coverage for RLS-2 (v3 audit): <see cref="TenantDissolutionService.DissolveAsync"/> enters the
/// TARGET tenant so its set-based teardown is scoped to it even when the caller's ambient tenant is a
/// different one (account erasure of a solo tenant that isn't the JWT-current tenant, admin paths). Runs as
/// the non-privileged runtime role, so the Postgres RLS backstop is live (superusers are exempt) — the same
/// harness as <see cref="RlsBackstopTests"/>.
/// <para>
/// Note: this is a regression/isolation guard, NOT a fail-first bug repro. On current code the split-brain
/// RLS-2 describes is already backstopped two ways — feature contributors wipe via <c>QueryAllTenants()</c>,
/// whose <c>CrossTenant</c> tag DOES render for <c>ExecuteDelete</c> and bypasses RLS; and the core
/// teardown's only RLS'd table (<see cref="TenantInvitation"/>) is FK-cascaded from the (non-RLS'd)
/// <c>Tenants</c> delete. <c>EnterTenant(target)</c> removes the dissolve's reliance on BOTH of those
/// implicit backstops (see the arch guard <c>TenantDissolution_EntersTheTargetTenant</c>). This test proves
/// a foreign-ambient dissolve wipes the target and leaves the ambient tenant untouched.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DissolveUnderForeignTenantTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly Guid _ambientForeign = Guid.CreateVersion7(); // the caller's ambient tenant (A) — kept
    private readonly Guid _target = Guid.CreateVersion7();          // the tenant being dissolved (B)
    private string RuntimeCs => RlsTestSetup.RuntimeConnectionString(fixture.ConnectionString);

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        // EnsureCreated schema has no RLS DDL — provision the runtime role AND the model-derived policies.
        await using (var db = fixture.CreateContext())
            await RlsTestSetup.ProvisionAsync(db);

        await SeedTenantAsync(_ambientForeign, "ambient-A");
        await SeedTenantAsync(_target, "target-B");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Dissolve_UnderForeignAmbient_WipesTarget_AndLeavesTheAmbientTenantUntouched()
    {
        // The caller's ambient tenant is the FOREIGN one; the dissolve targets a different tenant. A shared
        // instance backs both the context's RLS filter and the service's ITenantContext (exactly like the
        // scoped HttpCurrentTenant in production), so the service's EnterTenant(target) drives the GUC.
        var ambient = new TestCurrentTenant { TenantId = _ambientForeign };
        var options = new DbContextOptionsBuilder<TestAppDbContext>().UseNpgsql(RuntimeCs).Options;
        await using var db = new TestAppDbContext(options, ambient);

        // The harness TestWidget fixture stands in for a feature contributor (platform tests must not depend
        // on the DELETE-ME Notes sample — R9/TR-1).
        var contributors = new ITenantDataContributor[] { new TestWidgetDataContributor(new EfRepository<TestWidget>(db)) };
        var service = new TenantDissolutionService(contributors, new TenantRepository(db), ambient);

        await service.DissolveAsync(_target);

        // Read back as the (RLS-exempt) superuser.
        await using var read = fixture.CreateTestContext();
        // Target is fully gone: core row, its RLS'd invitation, and its contributor data.
        Assert.False(await read.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == _target));
        Assert.Empty(await read.Set<TenantInvitation>().IgnoreQueryFilters().Where(i => i.TenantId == _target).ToListAsync());
        Assert.Empty(await read.TestWidgets.IgnoreQueryFilters().Where(w => w.TenantId == _target).ToListAsync());
        // The caller's ambient tenant is completely untouched by the dissolve.
        Assert.True(await read.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == _ambientForeign));
        Assert.Single(await read.Set<TenantInvitation>().IgnoreQueryFilters().Where(i => i.TenantId == _ambientForeign).ToListAsync());
        Assert.Single(await read.TestWidgets.IgnoreQueryFilters().Where(w => w.TenantId == _ambientForeign).ToListAsync());
    }

    private async Task SeedTenantAsync(Guid tenantId, string name)
    {
        // Seed as the (RLS-exempt) superuser through the normal tenant-stamping path.
        await using var db = fixture.CreateTestContext(tenantId);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = name, CreatedAt = Now, UpdatedAt = Now });
        db.Set<TenantInvitation>().Add(new TenantInvitation
        {
            TenantId = tenantId,
            InvitedEmail = $"invitee@{name}.example",
            InvitedByUserId = Guid.CreateVersion7(),
            Status = InvitationStatuses.Pending,
            TokenHash = Guid.NewGuid().ToString("N"),
            CreatedAt = Now,
            ExpiresAt = Now.AddDays(7),
        });
        db.TestWidgets.Add(new TestWidget { Name = $"{name}-widget", CreatedAt = Now });
        await db.SaveChangesAsync();
    }

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;
}
