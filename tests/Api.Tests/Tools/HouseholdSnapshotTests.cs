using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Xunit;

namespace Vuelto.Api.Tests.Tools;

/// <summary>
/// The operator tool <c>tools/snapshot-household.sql</c> (hidden — no UI, no route): snapshot one household
/// from a source database as a SQL file, restore it on a target. Proven as a round-trip on the fixture:
/// seed → snapshot → wipe → restore → the same rows, ids and frozen rates. Plus a list gate: every
/// tenant-scoped table in the model must be named in the script — included or explicitly excluded — so a
/// new entity cannot silently fall out of the snapshot.
/// </summary>
[Collection(PostgresCollection.Name)]
public class HouseholdSnapshotTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Api", "Features"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root from the test assembly.");
    }

    private static string ScriptText() => File.ReadAllText(Path.Combine(RepoRoot(), "tools", "snapshot-household.sql"));

    [Fact]
    public void EveryTenantScopedTable_IsEitherIncludedOrExplicitlyExcluded()
    {
        var script = ScriptText();
        var included = Regex.Matches(script, @"\['([A-Za-z]+)',\s*'(?:tenantid|tenant|members)'\]").Select(m => m.Groups[1].Value).ToHashSet();
        var excludedLine = Regex.Match(script, @"Excluded on purpose:(.*?)`HouseholdSnapshotTests`", RegexOptions.Singleline).Groups[1].Value;
        var excluded = Regex.Matches(excludedLine, @"\b([A-Z][A-Za-z]+)\b").Select(m => m.Groups[1].Value).ToHashSet();

        using var db = Fixture.CreateContext();
        var tenantScopedTables = db.Model.GetEntityTypes()
            .Where(e => typeof(ITenantScoped).IsAssignableFrom(e.ClrType))
            .Select(e => e.GetTableName()!)
            .Where(t => t != "TestWidgets") // the fixture's own entity
            .ToHashSet();

        var missing = tenantScopedTables.Where(t => !included.Contains(t) && !excluded.Contains(t)).Order().ToList();
        Assert.True(missing.Count == 0, $"Tenant-scoped tables neither included nor explicitly excluded in tools/snapshot-household.sql: {string.Join(", ", missing)}");
        Assert.Contains("EmailConnections", excluded); // user-keyed, tokens bound to the source key ring
    }

    [Fact]
    public async Task Snapshot_ThenRestore_OnAnEmptyTarget_ReproducesTheHousehold()
    {
        // Seed a household: identity + budget data, with ids we can recognise on the far side.
        var tenant = Guid.CreateVersion7();
        var email = $"{Guid.NewGuid():N}@snapshot.test";
        var user = new User { Email = email, DisplayName = "Snapshot Owner", EmailVerified = true, CreatedAt = T0, UpdatedAt = T0 };
        Guid categoryId, bankId, monthId, txId, refundId;
        await using (var db = Fixture.CreateContext(tenant))
        {
            db.Add(user);
            db.Set<Tenant>().Add(new Tenant { Id = tenant, Name = "Snapshot household", CreatedAt = T0, UpdatedAt = T0 });
            db.Set<TenantMembership>().Add(new TenantMembership { TenantId = tenant, UserId = user.Id, Role = TenantRoles.Owner, JoinedAt = T0 });
            var category = new Category { TenantId = tenant, Name = "Salud", CreatedAt = T0, UpdatedAt = T0 };
            var bank = new Bank { TenantId = tenant, Name = "BAC", CreatedAt = T0, UpdatedAt = T0 };
            var month = new Month { TenantId = tenant, Year = 2026, MonthNumber = 9, WeekCount = 5, Week1StartDate = new DateOnly(2026, 8, 25), PrimaryIncomeAmount = 8950m, PrimaryIncomeCurrency = "USD", SecondaryIncomeCurrency = "USD", CreatedAt = T0, UpdatedAt = T0 };
            var week = new Week { TenantId = tenant, MonthId = month.Id, WeekNumber = 1, StartDate = new DateOnly(2026, 8, 25), EndDate = new DateOnly(2026, 8, 31) };
            var tx = new Transaction
            {
                TenantId = tenant, MonthId = month.Id, BankId = bank.Id, CategoryId = category.Id, Payee = "Farmacia", PaymentMethod = "credit_card",
                OriginalAmount = 20000m, Currency = "CRC", TransactionDate = new DateOnly(2026, 9, 4), AmountCrc = 20000m, AmountUsd = 44.14m,
                ExchangeRateUsed = 453.1262m, TransactionType = TransactionTypes.UnplannedEssential, Source = TransactionSources.Email, CreatedAt = T0, UpdatedAt = T0,
            };
            var refund = new Refund { TenantId = tenant, MonthId = month.Id, TransactionId = tx.Id, Payee = "Farmacia", TransactionDate = tx.TransactionDate, Percentage = 30m, AmountCrc = 6000m, AmountUsd = 13.24m, Status = RefundStatuses.Pending, CreatedAt = T0, UpdatedAt = T0 };
            db.AddRange(category, bank, month, week, tx, refund);
            db.Add(new BudgetSettings { TenantId = tenant, WeekStartWeekday = 2, MonthAnchor = MonthAnchors.LastWeekdayPrev, PrimaryIncome4w = 7150m, PrimaryIncome5w = 8950m, PrimaryIncomeCurrency = "USD", SecondaryIncomeCurrency = "USD", CreatedAt = T0, UpdatedAt = T0 });
            await db.SaveChangesAsync();
            categoryId = category.Id; bankId = bank.Id; monthId = month.Id; txId = tx.Id; refundId = refund.Id;
        }

        // Snapshot on one open connection (pg_temp functions live for the session).
        string snapshot;
        await using (var conn = new NpgsqlConnection(Fixture.ConnectionString))
        {
            await conn.OpenAsync();
            var script = ScriptText();
            var functionOnly = script[..script.LastIndexOf("SELECT pg_temp.snapshot_household", StringComparison.Ordinal)];
            await using (var create = new NpgsqlCommand(functionOnly, conn)) await create.ExecuteNonQueryAsync();
            await using var call = new NpgsqlCommand("SELECT pg_temp.snapshot_household(@email)", conn);
            call.Parameters.AddWithValue("email", email);
            snapshot = (string)(await call.ExecuteScalarAsync())!;
        }
        Assert.StartsWith("-- ¿Y el vuelto? household snapshot", snapshot);
        Assert.Contains("-- Transactions: 1 row(s)", snapshot);
        Assert.Contains("-- Refunds: 1 row(s)", snapshot);
        Assert.Contains("-- PendingVouchers: 0 row(s)", snapshot);
        Assert.DoesNotContain("EmailConnections", snapshot);

        // Wipe the household — the "empty target" — then restore from the text alone.
        await using (var db = Fixture.CreateContext())
        {
            foreach (var table in new[] { "Refunds", "Transactions", "Weeks", "Months", "BudgetSettings", "Banks", "Categories", "TenantMemberships" })
            {
                var sql = "DELETE FROM \"" + table + "\" WHERE \"TenantId\" = {0}"; // fixed table names from the list above, not user input
#pragma warning disable EF1002
                await db.Database.ExecuteSqlRawAsync(sql, tenant);
#pragma warning restore EF1002
            }
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Tenants\" WHERE \"Id\" = {0}", tenant);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Users\" WHERE \"Id\" = {0}", user.Id);
        }
        await using (var conn = new NpgsqlConnection(Fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var restore = new NpgsqlCommand(snapshot, conn);
            await restore.ExecuteNonQueryAsync();
            await using var again = new NpgsqlCommand(snapshot, conn); // idempotent: a second apply is a no-op
            await again.ExecuteNonQueryAsync();
        }

        // Same rows, same ids, same money.
        await using (var db = Fixture.CreateContext(tenant))
        {
            Assert.Equal(email, (await db.Set<User>().SingleAsync(u => u.Id == user.Id)).Email);
            Assert.Equal(TenantRoles.Owner, (await db.Set<TenantMembership>().SingleAsync(m => m.TenantId == tenant)).Role);
            Assert.Equal("Snapshot household", (await db.Set<Tenant>().SingleAsync(t => t.Id == tenant)).Name);
            Assert.Equal((2, MonthAnchors.LastWeekdayPrev, 8950m), await db.BudgetSettings.Where(s => s.TenantId == tenant).Select(s => new ValueTuple<int, string, decimal>(s.WeekStartWeekday, s.MonthAnchor, s.PrimaryIncome5w)).SingleAsync());
            Assert.Equal("Salud", (await db.Categories.SingleAsync(c => c.Id == categoryId)).Name);
            Assert.Equal("BAC", (await db.Banks.SingleAsync(b => b.Id == bankId)).Name);
            Assert.Equal((5, new DateOnly(2026, 8, 25)), await db.Months.Where(m => m.Id == monthId).Select(m => new ValueTuple<int, DateOnly>(m.WeekCount, m.Week1StartDate)).SingleAsync());
            Assert.Single(await db.Weeks.Where(w => w.MonthId == monthId).ToListAsync());
            var tx = await db.Transactions.SingleAsync(t => t.Id == txId);
            Assert.Equal((monthId, bankId, categoryId, 453.1262m, 20000m, 44.14m, TransactionSources.Email), (tx.MonthId, tx.BankId, tx.CategoryId, tx.ExchangeRateUsed, tx.AmountCrc, tx.AmountUsd, tx.Source));
            var refund = await db.Refunds.SingleAsync(r => r.Id == refundId);
            Assert.Equal((txId, 30m, 6000m, RefundStatuses.Pending), (refund.TransactionId, refund.Percentage, refund.AmountCrc, refund.Status));
        }
    }
}
