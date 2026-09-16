using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Api.Tests.Rls;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Api.Tests;

/// <summary>
/// INCOME-1 data safety (plan §4a) on a real database built by the real migrations: old-shape data is seeded at the
/// migration BEFORE <c>AddIncomeLines</c>, then the migration runs and must (1) turn each household's 4w/5w defaults into
/// the right lines, (2) copy every month's income verbatim — per currency, to the cent (the shipped
/// <c>tools/check-income-parity.sql</c> returns no rows), (3) leave every old column exactly as it was, (4) add nothing
/// when the shipped backfill runs again, (5) copy under row-level security only because it sets the bypass (a
/// non-superuser runs it — Neon's owner is one), and (6) roll back leaving the old data intact.
/// </summary>
public sealed class IncomeMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public Task InitializeAsync() => _container.StartAsync();
    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private static readonly Guid Weekly = Guid.Parse("11111111-0000-0000-0000-000000000001");     // 2000/2500 USD + 600000/600000 CRC
    private static readonly Guid Irregular = Guid.Parse("11111111-0000-0000-0000-000000000002");  // 1000/1300 CRC, no secondary
    private static readonly Guid NoSettings = Guid.Parse("11111111-0000-0000-0000-000000000003"); // never saved settings
    private static readonly Guid FiveOnly = Guid.Parse("11111111-0000-0000-0000-000000000004");   // 0/500 USD

    private AppDbContext Context()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_container.GetConnectionString()).Options;
        return new AppDbContext(options, new TestCurrentTenant());
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Vuelto.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Tool(string name) => File.ReadAllText(Path.Combine(RepoRoot(), "tools", name));

    private async Task<List<T>> QueryAsync<T>(string sql, Func<NpgsqlDataReader, T> map, string? connectionString = null)
    {
        await using var conn = new NpgsqlConnection(connectionString ?? _container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<T>();
        do
        {
            while (await reader.ReadAsync())
                if (reader.FieldCount > 1 || reader.GetName(0) != "set_config") rows.Add(map(reader));
        } while (await reader.NextResultAsync());
        return rows;
    }

    private async Task ExecAsync(string sql, string? connectionString = null)
    {
        await using var conn = new NpgsqlConnection(connectionString ?? _container.GetConnectionString());
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(sql, conn, tx)) await cmd.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    /// <summary>The migration right before <c>AddIncomeLines</c> — where the old shape is the whole truth.</summary>
    private static string PreviousMigration(AppDbContext db)
    {
        var all = db.Database.GetMigrations().ToList();
        var index = all.FindIndex(m => m.EndsWith("_AddIncomeLines", StringComparison.Ordinal));
        Assert.True(index > 0, "AddIncomeLines migration not found");
        return all[index - 1];
    }

    private const string SeedOldShape = """
        INSERT INTO "BudgetSettings" ("Id","TenantId","WeekStartWeekday","MonthAnchor",
            "PrimaryIncome4w","PrimaryIncome5w","PrimaryIncomeCurrency","SecondaryIncome4w","SecondaryIncome5w","SecondaryIncomeCurrency","CreatedAt","UpdatedAt")
        VALUES
          (gen_random_uuid(), '11111111-0000-0000-0000-000000000001', 2, 'last_weekday_prev', 2000, 2500, 'USD', 600000, 600000, 'CRC', now(), now()),
          (gen_random_uuid(), '11111111-0000-0000-0000-000000000002', 2, 'last_weekday_prev', 1000, 1300, 'CRC', 0, 0, 'USD', now(), now()),
          (gen_random_uuid(), '11111111-0000-0000-0000-000000000004', 2, 'last_weekday_prev', 0, 500, 'USD', 0, 0, 'USD', now(), now());

        INSERT INTO "Months" ("Id","TenantId","Year","MonthNumber","WeekCount","Week1StartDate",
            "PrimaryIncomeAmount","PrimaryIncomeCurrency","SecondaryIncomeAmount","SecondaryIncomeCurrency","CreatedAt","UpdatedAt")
        VALUES
          ('22222222-0000-0000-0000-000000000001', '11111111-0000-0000-0000-000000000001', 2026, 8, 4, '2026-07-28', 2000, 'USD', 600000, 'CRC', now(), now()),
          ('22222222-0000-0000-0000-000000000002', '11111111-0000-0000-0000-000000000001', 2026, 9, 5, '2026-08-25', 2500, 'USD', 600000, 'CRC', now(), now()),
          ('22222222-0000-0000-0000-000000000003', '11111111-0000-0000-0000-000000000001', 2026, 10, 4, '2026-09-29', 2222.22, 'USD', 0, 'CRC', now(), now()),
          ('22222222-0000-0000-0000-000000000004', '11111111-0000-0000-0000-000000000002', 2026, 9, 5, '2026-08-25', 1000, 'CRC', 50.5, 'USD', now(), now()),
          ('22222222-0000-0000-0000-000000000005', '11111111-0000-0000-0000-000000000003', 2026, 9, 5, '2026-08-25', 0, 'USD', 0, 'USD', now(), now()),
          ('22222222-0000-0000-0000-000000000006', '11111111-0000-0000-0000-000000000003', 2026, 10, 4, '2026-09-29', 0, 'CRC', 700000, 'CRC', now(), now());
        """;

    private const string LegacySnapshot = """
        SELECT 'm:' || "Id"::text || ':' || "PrimaryIncomeAmount"::text || ':' || "PrimaryIncomeCurrency" || ':' || "SecondaryIncomeAmount"::text || ':' || "SecondaryIncomeCurrency" FROM "Months"
        UNION ALL
        SELECT 's:' || "TenantId"::text || ':' || "PrimaryIncome4w"::text || ':' || "PrimaryIncome5w"::text || ':' || "PrimaryIncomeCurrency" || ':' || "SecondaryIncome4w"::text || ':' || "SecondaryIncome5w"::text || ':' || "SecondaryIncomeCurrency" FROM "BudgetSettings"
        ORDER BY 1
        """;

    private sealed record LineRow(Guid Tenant, string Name, string Period, decimal Amount, string Currency, bool NeedsReview, int Order);

    private Task<List<LineRow>> LinesAsync() => QueryAsync("""
        SELECT "TenantId", "Name", "PayPeriod", "Amount", "Currency", "NeedsReview", "SortOrder" FROM "IncomeLines" ORDER BY "TenantId", "SortOrder"
        """, r => new LineRow(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetString(4), r.GetBoolean(5), r.GetInt32(6)));

    private Task<List<string>> MonthRowsAsync() => QueryAsync("""
        SELECT r."MonthId"::text || '|' || r."Label" || '|' || r."Currency" || '|' || r."Amount"::text || '|' || r."PlannedAmount"::text || '|' || (r."IncomeLineId" IS NOT NULL)::text
        FROM "MonthIncomes" r ORDER BY r."MonthId", r."SortOrder"
        """, r => r.GetString(0));

    [Fact]
    public async Task TheMigration_CopiesEveryIncome_Verbatim_AndChangesNothingElse()
    {
        await using var db = Context();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration(db));
        await ExecAsync(SeedOldShape);
        var legacyBefore = await QueryAsync(LegacySnapshot, r => r.GetString(0));

        await migrator.MigrateAsync(); // AddIncomeLines and anything after it

        // (1) lines from the 4w/5w defaults
        Assert.Equal(
        [
            new(Weekly, "Primary income", "weekly", 500m, "USD", false, 0),        // 2500/5 == 2000/4
            new(Weekly, "Secondary income", "monthly", 600_000m, "CRC", false, 1), // 5w == 4w
            new(Irregular, "Primary income", "weekly", 250m, "CRC", true, 0),      // 1300/5 != 1000/4 → check it
            new(FiveOnly, "Primary income", "weekly", 100m, "USD", true, 0),       // 4w is zero → 5w/5, check it
        ], await LinesAsync());

        // (2) every month's income, verbatim; zero slots produce no row; a slot with no line is a row without a line
        Assert.Equal(
        [
            "22222222-0000-0000-0000-000000000001|Primary income|USD|2000.00|2000.00|true",
            "22222222-0000-0000-0000-000000000001|Secondary income|CRC|600000.00|600000.00|true",
            "22222222-0000-0000-0000-000000000002|Primary income|USD|2500.00|2500.00|true",
            "22222222-0000-0000-0000-000000000002|Secondary income|CRC|600000.00|600000.00|true",
            "22222222-0000-0000-0000-000000000003|Primary income|USD|2222.22|2222.22|true",
            "22222222-0000-0000-0000-000000000004|Primary income|CRC|1000.00|1000.00|true",
            "22222222-0000-0000-0000-000000000004|Secondary income|USD|50.50|50.50|false",
            "22222222-0000-0000-0000-000000000006|Secondary income|CRC|700000.00|700000.00|false",
        ], await MonthRowsAsync());
        Assert.Empty(await QueryAsync(Tool("check-income-parity.sql"), r => r.GetGuid(0)));

        // (3) the old columns are untouched
        Assert.Equal(legacyBefore, await QueryAsync(LegacySnapshot, r => r.GetString(0)));

        // (4) running the shipped backfill again adds nothing
        await ExecAsync(Tool("backfill-income-lines.sql"));
        Assert.Equal(4, (await LinesAsync()).Count);
        Assert.Equal(8, (await MonthRowsAsync()).Count);

        // A household restored after the migration (no rows yet) is picked up by the same script.
        await ExecAsync("""DELETE FROM "MonthIncomes" WHERE "MonthId" = '22222222-0000-0000-0000-000000000004'; DELETE FROM "IncomeLines" WHERE "TenantId" = '11111111-0000-0000-0000-000000000002';""");
        await ExecAsync(Tool("backfill-income-lines.sql"));
        Assert.Equal(8, (await MonthRowsAsync()).Count);
        Assert.Empty(await QueryAsync(Tool("check-income-parity.sql"), r => r.GetGuid(0)));

        // A parity break is caught.
        await ExecAsync("""UPDATE "MonthIncomes" SET "Amount" = "Amount" + 1 WHERE "MonthId" = '22222222-0000-0000-0000-000000000003';""");
        var broken = await QueryAsync(Tool("check-income-parity.sql"), r => (r.GetGuid(0), r.GetString(1)));
        Assert.Equal((Guid.Parse("22222222-0000-0000-0000-000000000003"), "USD"), Assert.Single(broken));
    }

    [Fact]
    public async Task UnderRowLevelSecurity_TheBackfillCopiesOnlyBecauseItSetsTheBypass()
    {
        await using var db = Context();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration(db));
        await ExecAsync(SeedOldShape);
        await migrator.MigrateAsync();
        await RlsTestSetup.ProvisionRuntimeRoleAsync(db);
        var runtime = RlsTestSetup.RuntimeConnectionString(_container.GetConnectionString());
        await ExecAsync("""DELETE FROM "MonthIncomes"; DELETE FROM "IncomeLines";""");

        // Without the bypass line a non-superuser sees no old rows under the forced policies — nothing is copied.
        var withoutBypass = Tool("backfill-income-lines.sql").Replace("SELECT set_config('app.rls_bypass', 'on', true);", "", StringComparison.Ordinal);
        Assert.NotEqual(Tool("backfill-income-lines.sql"), withoutBypass);
        await ExecAsync(withoutBypass, runtime);
        Assert.Empty(await LinesAsync());

        // The shipped script, as that same role, copies everything.
        await ExecAsync(Tool("backfill-income-lines.sql"), runtime);
        Assert.Equal(4, (await LinesAsync()).Count);
        Assert.Equal(8, (await MonthRowsAsync()).Count);

        // And the bypass was local to that transaction: a fresh command as the runtime role sees nothing.
        Assert.Empty(await QueryAsync("""SELECT "Id" FROM "IncomeLines" """, r => r.GetGuid(0), runtime));
    }

    [Fact]
    public async Task RollingBack_DropsOnlyTheNewTables()
    {
        await using var db = Context();
        var migrator = db.GetService<IMigrator>();
        var previous = PreviousMigration(db);
        await migrator.MigrateAsync(previous);
        await ExecAsync(SeedOldShape);
        var legacyBefore = await QueryAsync(LegacySnapshot, r => r.GetString(0));
        await migrator.MigrateAsync();

        await migrator.MigrateAsync(previous);

        Assert.Equal(legacyBefore, await QueryAsync(LegacySnapshot, r => r.GetString(0)));
        Assert.Empty(await QueryAsync("""SELECT table_name::text FROM information_schema.tables WHERE table_name IN ('IncomeLines', 'MonthIncomes')""", r => r.GetString(0)));
    }

    [Fact]
    public void TheShippedScript_IsTheMigrationsSql() =>
        Assert.Equal(Normalize(IncomeBackfill.Sql), Normalize(Tool("backfill-income-lines.sql")));

    private static string Normalize(string sql) => sql.Replace("\r\n", "\n").Trim();
}
