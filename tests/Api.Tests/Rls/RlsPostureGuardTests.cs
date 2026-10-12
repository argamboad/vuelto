using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Api.Tests.Rls;

/// <summary>
/// ADR-020: the posture guard must refuse privileged runtime connections (which Postgres exempts
/// from RLS) and accept the non-privileged runtime role.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RlsPostureGuardTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await using var db = fixture.CreateTestContext();
        await RlsTestSetup.ProvisionAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SuperuserConnection_Throws()
    {
        await using var db = fixture.CreateTestContext();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RlsPostureGuard.EnsureRuntimeRoleIsNotPrivilegedAsync(db));
        Assert.Contains("row-level security", ex.Message);
    }

    [Fact]
    public async Task RuntimeRoleConnection_Passes()
    {
        var options = new DbContextOptionsBuilder<TestAppDbContext>()
            .UseNpgsql(RlsTestSetup.RuntimeConnectionString(fixture.ConnectionString))
            .Options;
        await using var db = new TestAppDbContext(options, new TestCurrentTenant());

        await RlsPostureGuard.EnsureRuntimeRoleIsNotPrivilegedAsync(db); // must not throw
    }
}
