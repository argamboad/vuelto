using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Vuelto.Api.Tests.Rls;

/// <summary>
/// Proves the RLS migration-parity gate (<see cref="RlsMigrationGateTests"/>) actually FAILS for a missing
/// policy — the property v3 audit finding RLS-1 showed was silently absent, because the integration
/// harness back-filled every model-derived policy before the gate looked (a slice could ship an
/// <c>ITenantScoped</c> table with no RLS migration and still pass CI, reaching production unprotected).
/// <para>
/// Uses its OWN throwaway database: builds the schema from the REAL migrations, provisions the runtime role
/// WITHOUT policies (exactly like the fixed <see cref="IntegrationTestFactory"/>), then confirms the shared
/// probe is (a) clean when migrations supply every policy, and (b) reports the offending table when a policy
/// is dropped. If (b) ever comes back clean, the gate has gone tautological again.
/// </para>
/// </summary>
public sealed class RlsMigrationGateBitesTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    [Fact]
    public async Task Gate_IsClean_WhenMigrationsSupplyPolicies_ButReportsAMissingPolicy_WhenOneIsDropped()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_container.GetConnectionString()).Options;
        await using var db = new AppDbContext(options, new TestCurrentTenant());
        await db.Database.MigrateAsync();                   // schema + policies from the REAL migrations
        await RlsTestSetup.ProvisionRuntimeRoleAsync(db);   // role only — exactly like the fixed harness

        var tables = RlsDdl.TenantTables(db.Model);
        Assert.NotEmpty(tables);

        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();

        // (a) A migrations-only schema is clean — the gate stays green for correct code.
        Assert.Empty(await RlsSchemaProbe.FindPolicyViolationsAsync(conn, tables));

        // (b) Drop one table's policy — the gate MUST now report exactly that table's missing policy.
        var (victim, _) = tables[0];
        await using (var drop = new NpgsqlCommand($"""DROP POLICY {RlsDdl.PolicyName} ON "{victim}";""", conn))
            await drop.ExecuteNonQueryAsync();

        var failures = await RlsSchemaProbe.FindPolicyViolationsAsync(conn, tables);
        Assert.Contains(failures, f => f.Contains(victim) && f.Contains("policy"));
    }
}
