using Npgsql;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Api.Tests.Rls;

/// <summary>
/// Inspects a live database's <c>pg_class</c>/<c>pg_policies</c> catalogs and reports every tenant-scoped
/// table that is missing ENABLE / FORCE row-level security or the tenant-isolation policy. Shared by the
/// RLS migration-parity gate (<see cref="RlsMigrationGateTests"/>) and the test that proves the gate
/// actually bites (<see cref="RlsMigrationGateBitesTests"/>), so both exercise the identical check.
/// </summary>
public static class RlsSchemaProbe
{
    public static async Task<IReadOnlyList<string>> FindPolicyViolationsAsync(
        NpgsqlConnection conn, IEnumerable<(string Table, string TenantColumn)> tables)
    {
        var failures = new List<string>();
        foreach (var (table, _) in tables)
        {
            await using var cmd = new NpgsqlCommand(
                """
                SELECT c.relrowsecurity,
                       c.relforcerowsecurity,
                       EXISTS (SELECT FROM pg_policies p
                               WHERE p.schemaname = 'public' AND p.tablename = c.relname
                                 AND p.policyname = $2)
                FROM pg_class c
                WHERE c.relname = $1 AND c.relnamespace = 'public'::regnamespace;
                """, conn);
            cmd.Parameters.Add(new NpgsqlParameter { Value = table });
            cmd.Parameters.Add(new NpgsqlParameter { Value = RlsDdl.PolicyName });

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                failures.Add($"{table}: table not found in migrated schema");
                continue;
            }
            if (!reader.GetBoolean(0)) failures.Add($"{table}: ROW LEVEL SECURITY not enabled");
            if (!reader.GetBoolean(1)) failures.Add($"{table}: ROW LEVEL SECURITY not FORCEd");
            if (!reader.GetBoolean(2)) failures.Add($"{table}: policy '{RlsDdl.PolicyName}' missing");
        }
        return failures;
    }

    /// <summary>The shared-or-tenant sibling (Arch A4): ENABLE + FORCE and all FOUR command-scoped policies, by name — collapsing
    /// them back into one FOR ALL policy would let every tenant delete the shared rows, and a name-agnostic check would not notice.</summary>
    public static async Task<IReadOnlyList<string>> FindSharedOrTenantPolicyViolationsAsync(
        NpgsqlConnection conn, IEnumerable<(string Table, string TenantColumn)> tables)
    {
        var failures = new List<string>();
        foreach (var (table, _) in tables)
        {
            await using var cmd = new NpgsqlCommand(
                """
                SELECT c.relrowsecurity,
                       c.relforcerowsecurity,
                       COALESCE(ARRAY(SELECT p.policyname::text FROM pg_policies p
                                      WHERE p.schemaname = 'public' AND p.tablename = c.relname), '{}')
                FROM pg_class c
                WHERE c.relname = $1 AND c.relnamespace = 'public'::regnamespace;
                """, conn);
            cmd.Parameters.Add(new NpgsqlParameter { Value = table });
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) { failures.Add($"{table}: table not found in migrated schema"); continue; }
            if (!reader.GetBoolean(0)) failures.Add($"{table}: ROW LEVEL SECURITY not enabled");
            if (!reader.GetBoolean(1)) failures.Add($"{table}: ROW LEVEL SECURITY not FORCEd");
            var present = reader.GetFieldValue<string[]>(2);
            foreach (var expected in RlsDdl.SharedOrTenantPolicyNames.Where(n => !present.Contains(n)))
                failures.Add($"{table}: policy '{expected}' missing");
        }
        return failures;
    }
}
