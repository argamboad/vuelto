using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests;

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
        var pair = await TwoTenants.SeedAsync(async tenant =>
        {
            await using var seed = Fixture.CreateContext();
            seed.Set<TestWidget>().Add(new TestWidget { Id = Guid.CreateVersion7(), TenantId = tenant, Name = $"{tenant}" });
            await seed.SaveChangesAsync();
        });

        await using var asA = Fixture.CreateContext(pair.Mine);
        var repo = new EfRepository<TestWidget>(asA);

        // Scoped surface: only the current tenant's row.
        var scoped = await repo.Query().ToListAsync();
        Assert.Equal(pair.Mine, Assert.Single(scoped).TenantId);

        // Escape hatch: every tenant's rows.
        var all = await repo.QueryAllTenants().ToListAsync();
        Assert.Equal(2, all.Count);
    }
}
