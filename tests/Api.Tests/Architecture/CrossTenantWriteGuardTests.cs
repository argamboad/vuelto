namespace Vuelto.Api.Tests.Architecture;

/// <summary>
/// Proves the cross-tenant-write scanner (<see cref="CrossTenantWriteGuard"/>) flags the RLS-4/RLS-8
/// composition (<c>QueryAllTenants()</c> + a set-based write) and only that — cross-tenant reads and the
/// sanctioned <c>EnterTenant</c>+<c>IgnoreQueryFilters</c> pattern stay clean, and a comment that merely
/// mentions <c>QueryAllTenants</c> next to a write is not a false positive.
/// </summary>
public class CrossTenantWriteGuardTests
{
    [Fact]
    public void Flags_QueryAllTenants_ComposedWith_ExecuteDelete()
    {
        var sources = new[] { ("FooDataContributor.cs", "await repo.QueryAllTenants().Where(x => x.TenantId == t).ExecuteDeleteAsync(ct);") };
        Assert.Single(CrossTenantWriteGuard.FindOffenders(sources));
    }

    [Fact]
    public void Flags_QueryAllTenants_ComposedWith_ExecuteUpdate_AcrossLines()
    {
        var sources = new[] { ("ApiKeyService.cs", "await keys.QueryAllTenants()\n    .Where(k => k.Id == id)\n    .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), ct);") };
        Assert.Single(CrossTenantWriteGuard.FindOffenders(sources));
    }

    [Fact]
    public void Allows_QueryAllTenants_ReadOnly()
    {
        var sources = new[] { ("SweepJob.cs", "var rows = await repo.QueryAllTenants().Where(x => x.Active).ToListAsync(ct);") };
        Assert.Empty(CrossTenantWriteGuard.FindOffenders(sources));
    }

    [Fact]
    public void Allows_EnterTenant_Then_IgnoreQueryFilters_ExecuteUpdate()
    {
        // The sanctioned pattern (invitation accept): no QueryAllTenants tag — the caller entered the tenant.
        var sources = new[] { ("TenantInvitationRepository.cs", "await db.TenantInvitations.IgnoreQueryFilters().Where(i => i.Id == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, accepted), ct);") };
        Assert.Empty(CrossTenantWriteGuard.FindOffenders(sources));
    }

    [Fact]
    public void CommentMentioningQueryAllTenants_NextToAWrite_IsNotAFalsePositive()
    {
        // The real contributors carry a comment like this right above the Query()-based delete.
        var sources = new[]
        {
            ("BarDataContributor.cs",
             "// Query() (not QueryAllTenants): composing QueryAllTenants() with a set-based write is banned.\n"
             + "await repo.Query().Where(x => x.TenantId == t).ExecuteDeleteAsync(ct);"),
        };
        Assert.Empty(CrossTenantWriteGuard.FindOffenders(sources));
    }
}
