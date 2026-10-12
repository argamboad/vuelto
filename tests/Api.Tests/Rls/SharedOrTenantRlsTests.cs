using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Api.Tests.Rls;

/// <summary>
/// The database wall for the shared-or-tenant shape (Arch A4, ADR-020 amended): connected as the non-privileged
/// runtime role, past the EF filter (<c>IgnoreQueryFilters</c>, raw SQL), the four command-scoped policies of
/// <c>RlsDdl.SharedOrTenantStatementsFor</c> let a tenant read the shared rows and its own, write its own only, and
/// never touch a shared row — which only the bypass GUC of a tenant-less context may write. On the
/// <see cref="TestSharedWidget"/> fixture, so the platform proves the policies without owning such a table itself.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SharedOrTenantRlsTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly Guid _mine = Guid.CreateVersion7();
    private readonly Guid _theirs = Guid.CreateVersion7();
    private string RuntimeCs => RlsTestSetup.RuntimeConnectionString(fixture.ConnectionString);

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await using (var db = fixture.CreateTestContext())
            await RlsTestSetup.ProvisionAsync(db); // the runtime role + both policy families

        // Seeded as the (RLS-exempt) superuser: one shared row, one row per tenant.
        await using (var system = fixture.CreateTestContext())
        {
            system.TestSharedWidgets.Add(new TestSharedWidget { Name = "shared-gin" });
            await system.SaveChangesAsync();
        }
        await using (var mine = fixture.CreateTestContext(_mine))
        {
            mine.TestSharedWidgets.Add(new TestSharedWidget { Name = "my-infusion", TenantId = _mine });
            await mine.SaveChangesAsync();
        }
        await using (var theirs = fixture.CreateTestContext(_theirs))
        {
            theirs.TestSharedWidgets.Add(new TestSharedWidget { Name = "their-infusion", TenantId = _theirs });
            await theirs.SaveChangesAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A TestAppDbContext connected as the runtime role (subject to RLS); the session interceptor sets the GUCs.</summary>
    private TestAppDbContext RuntimeContext(Guid? tenantId)
    {
        var options = new DbContextOptionsBuilder<TestAppDbContext>().UseNpgsql(RuntimeCs).Options;
        return new TestAppDbContext(options, new TestCurrentTenant { TenantId = tenantId });
    }

    [Fact]
    public async Task Select_PastTheEfFilter_SeesSharedAndOwn_NeverAnotherTenants()
    {
        await using var db = RuntimeContext(_mine);
        var names = await db.TestSharedWidgets.IgnoreQueryFilters().Select(w => w.Name).OrderBy(n => n).ToListAsync();
        Assert.Equal(["my-infusion", "shared-gin"], names);
    }

    [Fact]
    public async Task Delete_CannotRemoveASharedRow_ButRemovesAnOwnOne()
    {
        Assert.Equal(0, await RawAsync(_mine, """DELETE FROM "TestSharedWidgets" WHERE "Name" = 'shared-gin'"""));
        Assert.Equal(0, await RawAsync(_mine, """DELETE FROM "TestSharedWidgets" WHERE "Name" = 'their-infusion'"""));
        Assert.Equal(1, await RawAsync(_mine, """DELETE FROM "TestSharedWidgets" WHERE "Name" = 'my-infusion'"""));
        Assert.True(await ExistsAsync("shared-gin"));
    }

    [Fact]
    public async Task Update_CannotRewriteASharedRow()
    {
        Assert.Equal(0, await RawAsync(_mine, """UPDATE "TestSharedWidgets" SET "Name" = 'hijacked' WHERE "Name" = 'shared-gin'"""));
        Assert.True(await ExistsAsync("shared-gin"));
    }

    [Fact]
    public async Task Insert_OfASharedRow_UnderATenant_IsRejectedByTheDatabase()
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            RawAsync(_mine, $"""INSERT INTO "TestSharedWidgets" ("Id", "TenantId", "Name", "CreatedAt") VALUES ('{Guid.CreateVersion7()}', NULL, 'sneaky', now())"""));
        Assert.Equal("42501", error.SqlState); // the insert policy admits owned rows only
    }

    [Fact]
    public async Task Insert_OfASharedRow_UnderTheBypass_Succeeds()
    {
        await using var conn = new NpgsqlConnection(RuntimeCs);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand($"SET LOCAL {RlsDdl.BypassGuc} = 'on'", conn, tx))
            await set.ExecuteNonQueryAsync();
        await using var insert = new NpgsqlCommand(
            $"""INSERT INTO "TestSharedWidgets" ("Id", "TenantId", "Name", "CreatedAt") VALUES ('{Guid.CreateVersion7()}', NULL, 'seeded-orgeat', now())""", conn, tx);
        Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        await tx.CommitAsync();
        Assert.True(await ExistsAsync("seeded-orgeat"));
    }

    [Fact]
    public async Task TenantlessContext_WritesASharedRow_ThroughEf_AndEveryTenantReadsIt()
    {
        await using (var system = RuntimeContext(null))
        {
            system.TestSharedWidgets.Add(new TestSharedWidget { Name = "shared-vermouth" }); // bypass GUC: the interceptor's tenant-less posture
            await system.SaveChangesAsync();
        }
        await using var theirs = RuntimeContext(_theirs);
        Assert.Contains("shared-vermouth", await theirs.TestSharedWidgets.Select(w => w.Name).ToListAsync());
    }

    private async Task<int> RawAsync(Guid tenantId, string sql)
    {
        await using var conn = new NpgsqlConnection(RuntimeCs);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand($"SET LOCAL {RlsDdl.TenantGuc} = '{tenantId}'", conn, tx))
            await set.ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        var affected = await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
        return affected;
    }

    private async Task<bool> ExistsAsync(string name)
    {
        await using var db = fixture.CreateTestContext(); // superuser: the plain truth
        return await db.TestSharedWidgets.IgnoreQueryFilters().AnyAsync(w => w.Name == name);
    }
}
