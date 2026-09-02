using Microsoft.EntityFrameworkCore;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Infrastructure.Repositories;

namespace Perezosoft.Api.Tests;

/// <summary>
/// The generic repository exposes two deliberately distinct read surfaces: <c>Query()</c> is
/// tenant-scoped (the normal feature path — can't leak across tenants) and the explicit,
/// rare, greppable <c>QueryAllTenants()</c> is the audited cross-tenant escape hatch used by
/// dissolve contributors (MITI-1 / B1-3).
/// </summary>
[Collection(PostgresCollection.Name)]
public class RepositoryScopingTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task Query_IsTenantScoped_QueryAllTenants_SeesEveryTenant()
    {
        var tenantA = Guid.CreateVersion7();
        var tenantB = Guid.CreateVersion7();

        await using (var seed = Fixture.CreateContext())
        {
            seed.Set<TestWidget>().Add(new TestWidget { Id = Guid.CreateVersion7(), TenantId = tenantA, Name = "A" });
            seed.Set<TestWidget>().Add(new TestWidget { Id = Guid.CreateVersion7(), TenantId = tenantB, Name = "B" });
            await seed.SaveChangesAsync();
        }

        await using var asA = Fixture.CreateContext(tenantA);
        var repo = new EfRepository<TestWidget>(asA);

        // Scoped surface: only the current tenant's row.
        var scoped = await repo.Query().ToListAsync();
        Assert.Single(scoped);
        Assert.Equal(tenantA, scoped[0].TenantId);

        // Escape hatch: every tenant's rows.
        var all = await repo.QueryAllTenants().ToListAsync();
        Assert.Equal(2, all.Count);
    }
}
