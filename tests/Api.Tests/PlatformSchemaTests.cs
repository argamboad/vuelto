using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Vuelto.Api.Tests.Architecture;
using Vuelto.Api.Tests.Infrastructure;
using Xunit.Abstractions;

namespace Vuelto.Api.Tests;

/// <summary>
/// Arch A5 (#365), R161: the platform publishes the shape its migrations build for its tables — columns, constraints,
/// indexes, RLS policies, as Postgres reports them — in <c>platform-schema.json</c>, and every repo's migrated database
/// must match it for those tables. Platform migrations are regenerated per app (the same change has a different
/// migration id in each repo), so until this gate nothing proved that an app's platform tables equalled the platform's;
/// equivalence rested on regenerating correctly each time. The file is extracted from the database the real migrations
/// build (the integration harness), never from the model, so both sides read the same catalog queries.
/// <para>On the platform, regenerate after a schema migration: <c>PLATFORM_SCHEMA_WRITE=1 dotnet test --filter
/// PlatformSchemaTests</c>. Downstream the file is a platform-class file the port brings; the gate then compares the
/// app's migrated database with it.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class PlatformSchemaTests(IntegrationTestFactory factory, ITestOutputHelper output)
{
    private const string SchemaFile = "platform-schema.json";
    private const string WriteSwitch = "PLATFORM_SCHEMA_WRITE";

    /// <summary>Tables of the platform's schema that are not the platform's: the sample slice, and EF's own history.</summary>
    private static readonly HashSet<string> NotPlatformTables = new(StringComparer.Ordinal) { "Notes", "__EFMigrationsHistory" };

    [Fact]
    public async Task MigratedDatabase_MatchesThePlatformSchema()
    {
        var root = RepoRoot();
        var path = Path.Combine(root, SchemaFile);
        var (stampCommit, _) = PlatformOwnership.ParseStamp(File.ReadAllText(Path.Combine(root, "platform-stamp.json")));
        var isPlatform = stampCommit is null;

        await using var conn = new NpgsqlConnection(factory.DatabaseConnectionString);
        await conn.OpenAsync();

        if (isPlatform && Environment.GetEnvironmentVariable(WriteSwitch) == "1")
        {
            // Regeneration: every public table but the sample's and EF's, as the platform's migrations built them.
            var all = (await TableNamesAsync(conn)).Where(t => !NotPlatformTables.Contains(t)).ToList();
            var schema = await ExtractAsync(conn, all);
            await File.WriteAllTextAsync(path, Render(schema), new System.Text.UTF8Encoding(false));
            output.WriteLine($"wrote {SchemaFile}: {all.Count} tables");
            return;
        }

        Assert.True(File.Exists(path), $"{SchemaFile} is missing — on the platform, regenerate it with {WriteSwitch}=1; downstream, port it from the platform");
        var expected = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var expectedTables = expected["tables"]!.AsObject().Select(p => p.Key).ToList();
        var actual = await ExtractAsync(conn, expectedTables);

        var failures = new List<string>();
        foreach (var table in expectedTables)
        {
            var want = expected["tables"]![table]!.AsObject();
            if (!actual.TryGetValue(table, out var have)) { failures.Add($"{table}: table missing from the migrated database"); continue; }
            foreach (var facet in new[] { "columns", "constraints", "indexes", "policies" })
            {
                var w = want[facet]!.ToJsonString();
                var h = have[facet]!.ToJsonString();
                if (w != h) failures.Add($"{table}.{facet}: differs\n      platform: {w}\n      here:     {h}");
            }
        }

        if (isPlatform)
        {
            // The platform's own gate is also the currency check: a new platform table, or a dropped one, means the file is stale.
            var all = (await TableNamesAsync(conn)).Where(t => !NotPlatformTables.Contains(t)).ToList();
            foreach (var missing in all.Except(expectedTables).Order()) failures.Add($"{missing}: a platform table {SchemaFile} does not list — regenerate it ({WriteSwitch}=1)");
            foreach (var gone in expectedTables.Except(all).Order()) failures.Add($"{gone}: listed in {SchemaFile} but no longer built by the migrations — regenerate it ({WriteSwitch}=1)");
        }

        output.WriteLine($"{expectedTables.Count} platform tables compared with the migrated database");
        Assert.True(failures.Count == 0,
            $"the migrated database disagrees with {SchemaFile} (Arch A5): on the platform regenerate the file after a schema migration ({WriteSwitch}=1); "
            + "downstream, port the platform's migration or bring the table back to the platform's shape:\n  " + string.Join("\n  ", failures));
    }

    // ── extraction: what Postgres says the migrations built ──

    private static async Task<List<string>> TableNamesAsync(NpgsqlConnection conn)
    {
        var names = new List<string>();
        await using var cmd = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) names.Add(r.GetString(0));
        return names;
    }

    private static async Task<Dictionary<string, JsonObject>> ExtractAsync(NpgsqlConnection conn, IEnumerable<string> tables)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var table in tables.Order(StringComparer.Ordinal))
        {
            var columns = new JsonArray();
            await using (var cmd = new NpgsqlCommand("""
                SELECT column_name, data_type, COALESCE(character_maximum_length::text, ''), COALESCE(numeric_precision::text, ''), COALESCE(numeric_scale::text, ''), is_nullable, COALESCE(column_default, '')
                FROM information_schema.columns WHERE table_schema = 'public' AND table_name = $1 ORDER BY ordinal_position
                """, conn))
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = table });
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    columns.Add(new JsonObject
                    {
                        ["name"] = r.GetString(0), ["type"] = r.GetString(1),
                        ["length"] = r.GetString(2), ["precision"] = r.GetString(3), ["scale"] = r.GetString(4),
                        ["nullable"] = r.GetString(5) == "YES", ["default"] = r.GetString(6),
                    });
            }
            if (columns.Count == 0) continue; // the table is not here

            var constraints = new JsonArray();
            await using (var cmd = new NpgsqlCommand("""
                SELECT tc.constraint_name, tc.constraint_type,
                       string_agg(kcu.column_name, ',' ORDER BY kcu.ordinal_position),
                       COALESCE((SELECT ccu.table_name || '(' || string_agg(ccu.column_name, ',' ORDER BY ccu.column_name) || ')'
                                 FROM information_schema.constraint_column_usage ccu WHERE ccu.constraint_name = tc.constraint_name AND tc.constraint_type = 'FOREIGN KEY'
                                 GROUP BY ccu.table_name), ''),
                       COALESCE((SELECT rc.delete_rule FROM information_schema.referential_constraints rc WHERE rc.constraint_name = tc.constraint_name), '')
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage kcu ON kcu.constraint_name = tc.constraint_name AND kcu.table_name = tc.table_name
                WHERE tc.table_schema = 'public' AND tc.table_name = $1 AND tc.constraint_type IN ('PRIMARY KEY', 'UNIQUE', 'FOREIGN KEY')
                GROUP BY tc.constraint_name, tc.constraint_type ORDER BY tc.constraint_type, tc.constraint_name
                """, conn))
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = table });
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    constraints.Add(new JsonObject { ["name"] = r.GetString(0), ["type"] = r.GetString(1), ["columns"] = r.GetString(2), ["references"] = r.GetString(3), ["onDelete"] = r.GetString(4) });
            }

            var indexes = new JsonArray();
            await using (var cmd = new NpgsqlCommand("SELECT indexname, indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename = $1 ORDER BY indexname", conn))
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = table });
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    indexes.Add(new JsonObject { ["name"] = r.GetString(0), ["definition"] = r.GetString(1) });
            }

            var policies = new JsonArray();
            await using (var cmd = new NpgsqlCommand("""
                SELECT p.policyname, p.cmd, COALESCE(p.qual, ''), COALESCE(p.with_check, ''), c.relrowsecurity, c.relforcerowsecurity
                FROM pg_policies p JOIN pg_class c ON c.relname = p.tablename AND c.relnamespace = 'public'::regnamespace
                WHERE p.schemaname = 'public' AND p.tablename = $1 ORDER BY p.policyname
                """, conn))
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = table });
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    policies.Add(new JsonObject { ["name"] = r.GetString(0), ["command"] = r.GetString(1), ["using"] = r.GetString(2), ["withCheck"] = r.GetString(3), ["enabled"] = r.GetBoolean(4), ["forced"] = r.GetBoolean(5) });
            }

            result[table] = new JsonObject { ["columns"] = columns, ["constraints"] = constraints, ["indexes"] = indexes, ["policies"] = policies };
        }
        return result;
    }

    private static string Render(Dictionary<string, JsonObject> schema)
    {
        var tables = new JsonObject();
        foreach (var (name, shape) in schema.OrderBy(kv => kv.Key, StringComparer.Ordinal)) tables[name] = shape;
        var doc = new JsonObject
        {
            ["$comment"] = new JsonArray(
                "The shape the platform's migrations build for the platform's tables, as Postgres reports it (Arch A5, R161): columns,",
                "constraints, indexes, row-level-security policies. Written by PlatformSchemaTests with PLATFORM_SCHEMA_WRITE=1 on the",
                "platform after a schema migration; never edited by hand. Downstream, the same test compares the app's migrated",
                "database with this file for these tables — the app's own tables are not listed and not checked."),
            ["tables"] = tables,
        };
        return doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.EnumerateFiles("*.slnx").Any()) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
