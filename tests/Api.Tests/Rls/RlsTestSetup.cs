using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Api.Tests.Rls;

/// <summary>
/// Provisions what the RLS backstop (ADR-020) needs on a test database: the non-privileged runtime
/// role (local containers connect as a superuser, which Postgres exempts from RLS entirely — so the
/// backstop is only observable through a dedicated role) and the model-derived policies from
/// <see cref="RlsDdl"/>. Idempotent; safe to run per test.
/// </summary>
public static class RlsTestSetup
{
    public const string RuntimeRole = "app_runtime";
    public const string RuntimePassword = "rls-test-password";

    /// <summary>Connection string for the same database, connecting as the runtime role.</summary>
    public static string RuntimeConnectionString(string superuserConnectionString) =>
        new NpgsqlConnectionStringBuilder(superuserConnectionString)
        {
            Username = RuntimeRole,
            Password = RuntimePassword,
        }.ConnectionString;

    /// <summary>
    /// Creates the non-privileged runtime role (idempotent) and grants it CRUD — but does NOT apply any
    /// RLS policy. This is the <b>only</b> provisioning the migration-based harness may use
    /// (<see cref="Infrastructure.IntegrationTestFactory"/>): on a database built by the real migrations,
    /// the policies must come from those migrations alone, or the RLS migration-parity gate
    /// (<see cref="RlsMigrationGateTests"/>) would inspect a database whose policies the harness — not the
    /// migrations — supplied, and could never catch a missing-policy migration (v3 audit RLS-1). Enforced
    /// by an architecture test (<c>IntegrationFactory_DoesNotBackfillRlsPolicies</c>).
    /// </summary>
    public static async Task ProvisionRuntimeRoleAsync(DbContext db)
    {
        string[] statements =
        [
            $"""
             DO $$
             BEGIN
                 IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{RuntimeRole}') THEN
                     CREATE ROLE {RuntimeRole} LOGIN PASSWORD '{RuntimePassword}';
                 END IF;
             END $$;
             """,
            $"GRANT USAGE ON SCHEMA public TO {RuntimeRole};",
            $"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO {RuntimeRole};",
            $"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO {RuntimeRole};",
        ];
        foreach (var sql in statements)
#pragma warning disable EF1002 // no user input — role name/password are test constants
            await db.Database.ExecuteSqlRawAsync(sql);
#pragma warning restore EF1002
    }

    /// <summary>
    /// Runtime role + grants AND the model-derived RLS policies from <see cref="RlsDdl"/>. For harnesses
    /// that build the schema from the model via <c>EnsureCreated</c> (which does not emit RLS DDL) rather
    /// than migrations — e.g. <see cref="RlsBackstopTests"/>. Idempotent. **Never use this on a
    /// migration-built database** (see <see cref="ProvisionRuntimeRoleAsync"/>).
    /// </summary>
    public static async Task ProvisionAsync(DbContext db)
    {
        await ProvisionRuntimeRoleAsync(db);
        foreach (var sql in RlsDdl.StatementsFor(db.Model))
#pragma warning disable EF1002 // no user input — DDL is model-derived
            await db.Database.ExecuteSqlRawAsync(sql);
#pragma warning restore EF1002
    }
}
