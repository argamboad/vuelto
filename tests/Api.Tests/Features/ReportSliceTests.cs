using System.Text;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Features.Reports;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Features;

/// <summary>
/// REPORTS-1/2 on real Postgres: period resolution (month window from the LAST week's end date — WU-4 A3;
/// range validation codes; unknown/foreign month = not found), the analysis over tenant-filtered rows
/// with all-states names and single-month budget decoration, and the export (ordering, category/class
/// filters, tenant isolation, header-only, stored through IFileStorage with a signed link and a dated
/// filename from the injected clock).
/// </summary>
[Collection(PostgresCollection.Name)]
public class ReportSliceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Captures the stored object instead of touching disk; mints a predictable link.</summary>
    private sealed class CapturingFileStorage : IFileStorage
    {
        public readonly Dictionary<string, (string ContentType, byte[] Bytes)> Stored = new();
        public TimeSpan? LastLifetime;

        public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, cancellationToken);
            Stored[key] = (contentType, ms.ToArray());
        }
        public Task<FileObject?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<FileObject?>(null);
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Stored.ContainsKey(key));
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) { Stored.Remove(key); return Task.CompletedTask; }
        public Task<Uri> GetDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken = default)
        {
            LastLifetime = lifetime;
            return Task.FromResult(new Uri($"https://api.test/api/files/tok-{Stored.Count}"));
        }
    }

    private sealed record Ctx(AppDbContext Db, Guid Tenant, ReportHandler Handler, CapturingFileStorage Files, Guid MonthId, Guid Groceries, Guid Dining, Guid Bac);

    private async Task<Ctx> SeedAsync(bool firstOfMonthAnchor = false)
    {
        var tenant = Guid.CreateVersion7();
        var db = Fixture.CreateContext(tenant);

        var groceries = new Category { TenantId = tenant, Name = "Groceries", CreatedAt = T0, UpdatedAt = T0 };
        var dining = new Category { TenantId = tenant, Name = "Dining (old)", IsActive = false, CreatedAt = T0, UpdatedAt = T0 };
        var bac = new Bank { TenantId = tenant, Name = "BAC", CreatedAt = T0, UpdatedAt = T0 };
        Month month; List<Week> weeks;
        if (firstOfMonthAnchor)
        {
            month = new Month { TenantId = tenant, Year = 2026, MonthNumber = 6, WeekCount = 5, Week1StartDate = new DateOnly(2026, 6, 1), CreatedAt = T0, UpdatedAt = T0 };
            weeks =
            [
                W(1, new(2026, 6, 1), new(2026, 6, 7)), W(2, new(2026, 6, 8), new(2026, 6, 14)), W(3, new(2026, 6, 15), new(2026, 6, 21)),
                W(4, new(2026, 6, 22), new(2026, 6, 28)), W(5, new(2026, 6, 29), new(2026, 6, 30)) // clamps to the calendar month
            ];
        }
        else
        {
            month = new Month { TenantId = tenant, Year = 2026, MonthNumber = 6, WeekCount = 4, Week1StartDate = new DateOnly(2026, 5, 28), CreatedAt = T0, UpdatedAt = T0 };
            weeks = Enumerable.Range(0, 4).Select(i => W(i + 1, new DateOnly(2026, 5, 28).AddDays(7 * i), new DateOnly(2026, 6, 3).AddDays(7 * i))).ToList();
        }
        Week W(int n, DateOnly s, DateOnly e) => new() { TenantId = tenant, MonthId = month.Id, WeekNumber = n, StartDate = s, EndDate = e };

        db.AddRange(groceries, dining, bac, month); db.AddRange(weeks);
        db.Add(new FixedExpense { TenantId = tenant, Name = "Supermarket", CategoryId = groceries.Id, BudgetCrc = 60_000m, PaymentMethod = "credit_card", BankId = bac.Id, CreatedAt = T0, UpdatedAt = T0 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var files = new CapturingFileStorage();
        var handler = new ReportHandler(
            new EfRepository<Month>(db), new EfRepository<Week>(db), new EfRepository<Transaction>(db), new EfRepository<Category>(db),
            new EfRepository<Bank>(db), new EfRepository<FixedExpense>(db), new EfRepository<VariableExpense>(db),
            files, new FakeTimeProvider(T0));
        return new Ctx(db, tenant, handler, files, month.Id, groceries.Id, dining.Id, bac.Id);
    }

    private static async Task AddTxAsync(Ctx c, DateOnly date, decimal crc, string type = "budgeted", Guid? category = null, string payee = "Super MAS", DateTimeOffset? created = null)
    {
        c.Db.Add(new Transaction
        {
            TenantId = c.Tenant, MonthId = c.MonthId, BankId = c.Bac, CategoryId = category ?? c.Groceries, Payee = payee, PaymentMethod = "credit_card",
            OriginalAmount = crc, Currency = "CRC", TransactionDate = date, AmountCrc = crc, AmountUsd = crc / 500m, ExchangeRateUsed = 500m,
            TransactionType = type, CreatedAt = created ?? T0, UpdatedAt = created ?? T0
        });
        await c.Db.SaveChangesAsync();
        c.Db.ChangeTracker.Clear();
    }

    // ---- period resolution ----

    [Theory]
    [InlineData(null, null, null, "period_required")]
    [InlineData("m", "2026-06-01", "2026-06-30", "period_ambiguous")]
    [InlineData(null, "2026-06-01", null, "period_incomplete")]
    [InlineData(null, null, "2026-06-30", "period_incomplete")]
    [InlineData(null, "not-a-date", "2026-06-30", "period_invalid")]
    [InlineData(null, "2026-06-30", "2026-06-01", "period_invalid")]
    public async Task ResolvePeriod_RejectsBadInput_WithTheDonorCodes(string? month, string? from, string? to, string code)
    {
        var c = await SeedAsync();
        var r = await c.Handler.ResolvePeriodAsync(month is null ? null : c.MonthId, from, to, default);
        Assert.Null(r.Period);
        Assert.Equal(code, r.Error!.Error);
    }

    [Fact]
    public async Task ResolvePeriod_Month_UsesTheAnchorWindow()
    {
        var c = await SeedAsync();
        var r = await c.Handler.ResolvePeriodAsync(c.MonthId, null, null, default);
        Assert.Equal((new DateOnly(2026, 5, 28), new DateOnly(2026, 6, 24), true), (r.Period!.From, r.Period.To, r.Period.SingleMonth));
    }

    [Fact]
    public async Task ResolvePeriod_FirstOfMonthAnchor_EndsOnTheLastWeeksEndDate_NotSevenTimesWeekCount()
    {
        var c = await SeedAsync(firstOfMonthAnchor: true);
        var r = await c.Handler.ResolvePeriodAsync(c.MonthId, null, null, default);
        Assert.Equal(new DateOnly(2026, 6, 30), r.Period!.To); // WeekCount*7-1 would say Jul 5
    }

    [Fact]
    public async Task ResolvePeriod_UnknownOrForeignMonth_IsNotFound()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();
        Assert.True((await a.Handler.ResolvePeriodAsync(Guid.CreateVersion7(), null, null, default)).NotFound);
        Assert.True((await a.Handler.ResolvePeriodAsync(b.MonthId, null, null, default)).NotFound);
    }

    [Fact]
    public async Task ResolvePeriod_Range_IsInclusiveAndNotSingleMonth()
    {
        var c = await SeedAsync();
        var r = await c.Handler.ResolvePeriodAsync(null, "2026-01-01", "2026-06-30", default);
        Assert.Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30), false), (r.Period!.From, r.Period.To, r.Period.SingleMonth));
    }

    // ---- analysis ----

    [Fact]
    public async Task Analyze_SingleMonth_GroupsWithNamesAndBudget_FiltersToTheWindow()
    {
        var c = await SeedAsync();
        await AddTxAsync(c, new DateOnly(2026, 5, 27), 1_000m);                         // before the window
        await AddTxAsync(c, new DateOnly(2026, 5, 28), 5_000m);                         // first day
        await AddTxAsync(c, new DateOnly(2026, 6, 24), 3_000m);                         // last day
        await AddTxAsync(c, new DateOnly(2026, 6, 25), 4_000m);                         // after
        await AddTxAsync(c, new DateOnly(2026, 6, 10), 2_000m, "extraordinary", c.Dining);
        await AddTxAsync(c, new DateOnly(2026, 6, 10), 9_000m, "inflow");

        var period = (await c.Handler.ResolvePeriodAsync(c.MonthId, null, null, default)).Period!;
        var report = await c.Handler.AnalyzeAsync(period, default);

        Assert.True(report.SingleMonth);
        var groceries = Assert.Single(report.Budgeted);
        Assert.Equal(("Groceries", 8_000m, 16m, 60_000m, 0m), (groceries.CategoryName, groceries.TotalCrc, groceries.TotalUsd, groceries.BudgetedCrc, groceries.BudgetedUsd));
        var dining = Assert.Single(report.Extraordinary);
        Assert.Equal(("Dining (old)", 2_000m), (dining.CategoryName, dining.TotalCrc)); // inactive category still named
        Assert.Empty(report.UnplannedEssential);
    }

    [Fact]
    public async Task Analyze_Range_OmitsBudget_AndSeesOnlyThisTenant()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();
        await AddTxAsync(a, new DateOnly(2026, 6, 10), 5_000m);
        await AddTxAsync(b, new DateOnly(2026, 6, 10), 99_000m, payee: "OTHER");

        var report = await a.Handler.AnalyzeAsync(new ReportPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), null), default);

        var entry = Assert.Single(report.Budgeted);
        Assert.Equal(5_000m, entry.TotalCrc);
        Assert.Null(entry.BudgetedCrc);
        Assert.False(report.SingleMonth);
    }

    // ---- export ----

    [Fact]
    public async Task Export_StoresTheCsv_OrderedDateDescThenCreatedDesc_WithNames_AndMintsALink()
    {
        var c = await SeedAsync();
        await AddTxAsync(c, new DateOnly(2026, 6, 5), 1_000m, payee: "Older");
        await AddTxAsync(c, new DateOnly(2026, 6, 20), 2_000m, payee: "NewerFirstSaved", created: T0.AddMinutes(-5));
        await AddTxAsync(c, new DateOnly(2026, 6, 20), 3_000m, payee: "NewerLastSaved", category: c.Dining, created: T0);

        var period = (await c.Handler.ResolvePeriodAsync(c.MonthId, null, null, default)).Period!;
        var result = await c.Handler.ExportAsync(period, null, null, default);

        Assert.Equal(("transactions-2026-09-03.csv", 3, "https://api.test/api/files/tok-1", 900), (result.FileName, result.RowCount, result.DownloadUrl, result.ExpiresInSeconds));
        Assert.Equal(ReportHandler.LinkLifetime, c.Files.LastLifetime);
        var (key, stored) = Assert.Single(c.Files.Stored);
        Assert.StartsWith("exports/transactions/", key);
        Assert.EndsWith("/transactions-2026-09-03.csv", key);
        Assert.StartsWith("text/csv", stored.ContentType);

        var lines = Encoding.UTF8.GetString(stored.Bytes).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("2026-06-20,NewerLastSaved,Dining (old),budgeted,3000.00,6.00,500.0000,credit_card,BAC,manual", lines[1]);
        Assert.StartsWith("2026-06-20,NewerFirstSaved,Groceries", lines[2]);
        Assert.StartsWith("2026-06-05,Older", lines[3]);
    }

    [Fact]
    public async Task Export_Filters_ByCategoryAndClass_AndIsHeaderOnlyWhenNothingMatches()
    {
        var c = await SeedAsync();
        await AddTxAsync(c, new DateOnly(2026, 6, 5), 1_000m, payee: "GROC");
        await AddTxAsync(c, new DateOnly(2026, 6, 6), 2_000m, "extraordinary", c.Dining, payee: "DINE");
        var period = (await c.Handler.ResolvePeriodAsync(c.MonthId, null, null, default)).Period!;

        var byCategory = Csv(c, await c.Handler.ExportAsync(period, c.Groceries, null, default));
        Assert.Contains("GROC", byCategory); Assert.DoesNotContain("DINE", byCategory);

        var byClass = Csv(c, await c.Handler.ExportAsync(period, null, "Extraordinary", default)); // normalized
        Assert.Contains("DINE", byClass); Assert.DoesNotContain("GROC", byClass);

        var none = await c.Handler.ExportAsync(period, c.Groceries, "extraordinary", default);
        Assert.Equal(0, none.RowCount);
        Assert.Single(Csv(c, none).Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task Export_NeverIncludesAnotherTenantsRows()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();
        await AddTxAsync(a, new DateOnly(2026, 6, 5), 1_000m, payee: "MINE");
        await AddTxAsync(b, new DateOnly(2026, 6, 5), 1_000m, payee: "STOLEN");

        var csv = Csv(a, await a.Handler.ExportAsync(new ReportPeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), null), null, null, default));
        Assert.Contains("MINE", csv); Assert.DoesNotContain("STOLEN", csv);
    }

    private static string Csv(Ctx c, TransactionExportResponse r) =>
        Encoding.UTF8.GetString(c.Files.Stored.Values.Last().Bytes);
}
