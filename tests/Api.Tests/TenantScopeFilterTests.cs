using Microsoft.EntityFrameworkCore;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Core.Entities;

namespace Perezosoft.Api.Tests;

/// <summary>
/// Proves the EF global query filter makes tenant isolation structural: a tenant-scoped
/// entity (<see cref="TenantInvitation"/>) is only visible to its own tenant by default,
/// the filter fails closed when there's no current tenant, and genuinely cross-tenant
/// lookups can still opt out with IgnoreQueryFilters().
/// </summary>
[Collection(PostgresCollection.Name)]
public class TenantScopeFilterTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task ScopedEntity_IsVisibleOnlyToItsOwnTenant()
    {
        var (tenantA, tenantB) = await SeedTwoTenantsEachWithAnInviteAsync();

        await using var asA = Fixture.CreateContext(tenantA);
        var visible = await asA.TenantInvitations.ToListAsync();

        Assert.Single(visible);
        Assert.Equal(tenantA, visible[0].TenantId);
    }

    [Fact]
    public async Task IgnoreQueryFilters_SeesAllTenants()
    {
        await SeedTwoTenantsEachWithAnInviteAsync();

        await using var asA = Fixture.CreateContext(Guid.CreateVersion7()); // arbitrary current tenant
        var all = await asA.TenantInvitations.IgnoreQueryFilters().ToListAsync();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task NoCurrentTenant_SeesNoScopedRows()
    {
        await SeedTwoTenantsEachWithAnInviteAsync();

        await using var anon = Fixture.CreateContext(tenantId: null); // fail closed
        Assert.Empty(await anon.TenantInvitations.ToListAsync());
    }

    private async Task<(Guid TenantA, Guid TenantB)> SeedTwoTenantsEachWithAnInviteAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();

        // Seed with an unscoped context so both tenants' rows are written.
        await using var seed = Fixture.CreateContext();
        seed.Tenants.AddRange(
            new Tenant { Id = tenantA, Name = "A", CreatedAt = now, UpdatedAt = now },
            new Tenant { Id = tenantB, Name = "B", CreatedAt = now, UpdatedAt = now });
        seed.TenantInvitations.AddRange(
            NewInvite(tenantA, "a@example.com", now),
            NewInvite(tenantB, "b@example.com", now));
        await seed.SaveChangesAsync();

        return (tenantA, tenantB);
    }

    private static TenantInvitation NewInvite(Guid tenantId, string email, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        InvitedEmail = email,
        InvitedByUserId = Guid.CreateVersion7(),
        Status = InvitationStatuses.Pending,
        TokenHash = Guid.NewGuid().ToString("N"),
        CreatedAt = now,
        ExpiresAt = now.AddDays(7),
    };
}
