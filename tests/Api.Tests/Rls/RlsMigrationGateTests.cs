using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Api.Tests.Rls;

/// <summary>
/// B11-style enforcement gate for ADR-020: on a database built by the REAL migrations (the
/// integration harness), every <c>ITenantScoped</c> table must have row-level security enabled,
/// forced, and carry the tenant-isolation policy. Adding an <c>ITenantScoped</c> entity without
/// shipping its RLS policy migration fails here — the fail-closed property cannot silently regress.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class RlsMigrationGateTests(IntegrationTestFactory factory)
{
    [Fact]
    public async Task EveryTenantScopedTable_HasForcedRlsAndPolicy_AfterMigrations()
    {
        using var scope = factory.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<AppDbContext>().Model;
        var tables = RlsDdl.TenantTables(model);
        Assert.NotEmpty(tables);

        await using var conn = new NpgsqlConnection(factory.DatabaseConnectionString);
        await conn.OpenAsync();

        var failures = await RlsSchemaProbe.FindPolicyViolationsAsync(conn, tables);

        Assert.True(failures.Count == 0,
            "RLS backstop (ADR-020) is incomplete on the migrated schema — every ITenantScoped table "
            + "needs ENABLE + FORCE ROW LEVEL SECURITY and the tenant-isolation policy (add a migration "
            + $"using RlsDdl.StatementsFor):\n - {string.Join("\n - ", failures)}");
    }
}
