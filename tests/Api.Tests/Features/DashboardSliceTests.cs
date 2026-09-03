using Vuelto.Api.Features.Dashboard;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Features;

/// <summary>
/// DASH-1 on real Postgres: the handler reads one month's inputs through the tenant filter (weeks,
/// transactions, refunds, envelopes, both line lists, ALL categories and banks for names), resolves the
/// rate through the chain, and hands the pure service the exact inputs; no rate → summary null +
/// rate_unavailable; another household's month → null (uniform 404); an inactive category still names
/// its "other spending" row.
/// </summary>
[Collection(PostgresCollection.Name)]
public class DashboardSliceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedRate(decimal? rate) : IExchangeRateResolver
    {
        public Task<ResolvedRate?> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(rate is { } r ? new ResolvedRate(r, RateSources.Cache, T0) : null);
    }

    private sealed record Ctx(AppDbContext Db, Guid Tenant, Guid MonthId, DashboardHandler Handler);

    private async Task<Ctx> SeedAsync(decimal? rate = 500m)
    {
        var tenant = Guid.CreateVersion7();
        var db = Fixture.CreateContext(tenant);

        var housing = new Category { TenantId = tenant, Name = "Housing", CreatedAt = T0, UpdatedAt = T0 };
        var dining = new Category { TenantId = tenant, Name = "Dining (old)", IsActive = false, CreatedAt = T0, UpdatedAt = T0 };
        var bac = new Bank { TenantId = tenant, Name = "BAC", CreatedAt = T0, UpdatedAt = T0 };
        var month = new Month { TenantId = tenant, Year = 2026, MonthNumber = 6, WeekCount = 4, Week1StartDate = new DateOnly(2026, 5, 28), PrimaryIncomeAmount = 3000m, PrimaryIncomeCurrency = "USD", SecondaryIncomeAmount = 0m, SecondaryIncomeCurrency = "USD", CreatedAt = T0, UpdatedAt = T0 };
        var weeks = Enumerable.Range(0, 4).Select(i => new Week { TenantId = tenant, MonthId = month.Id, WeekNumber = i + 1, StartDate = new DateOnly(2026, 5, 28).AddDays(7 * i), EndDate = new DateOnly(2026, 6, 3).AddDays(7 * i) });
        var mortgage = new Transaction { TenantId = tenant, MonthId = month.Id, BankId = bac.Id, CategoryId = housing.Id, Payee = "Bank", PaymentMethod = "bank_account", OriginalAmount = 300_000m, Currency = "CRC", TransactionDate = new DateOnly(2026, 6, 5), AmountCrc = 300_000m, AmountUsd = 600m, ExchangeRateUsed = 500m, TransactionType = "budgeted", CreatedAt = T0, UpdatedAt = T0 };
        var lunch = new Transaction { TenantId = tenant, MonthId = month.Id, BankId = bac.Id, CategoryId = dining.Id, Payee = "Soda", PaymentMethod = "credit_card", OriginalAmount = 10_000m, Currency = "CRC", TransactionDate = new DateOnly(2026, 6, 12), AmountCrc = 10_000m, AmountUsd = 20m, ExchangeRateUsed = 500m, TransactionType = "unplanned_essential", CreatedAt = T0, UpdatedAt = T0 };
        var refund = new Refund { TenantId = tenant, MonthId = month.Id, TransactionId = lunch.Id, Payee = "Soda", TransactionDate = lunch.TransactionDate, Percentage = 50m, AmountCrc = 5_000m, AmountUsd = 10m, CreatedAt = T0, UpdatedAt = T0 };
        var line = new FixedExpense { TenantId = tenant, Name = "Mortgage", BudgetCrc = 350_000m, PaymentMethod = "bank_account", CategoryId = housing.Id, BankId = bac.Id, CreatedAt = T0, UpdatedAt = T0 };
        var envelope = new Envelope { TenantId = tenant, Name = "Marchamo", AnnualTargetCrc = 718_000m, CreatedAt = T0, UpdatedAt = T0 };

        db.AddRange(housing, dining, bac, month); db.AddRange(weeks); db.AddRange(mortgage, lunch, refund, line, envelope);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var current = new TestCurrentTenant { TenantId = tenant };
        var handler = new DashboardHandler(
            new EfRepository<Month>(db), new EfRepository<Week>(db), new EfRepository<Transaction>(db), new EfRepository<Refund>(db),
            new EfRepository<Envelope>(db), new EfRepository<FixedExpense>(db), new EfRepository<VariableExpense>(db),
            new EfRepository<Category>(db), new EfRepository<Bank>(db), new DashboardSummaryService(), new FixedRate(rate), current);
        return new Ctx(db, tenant, month.Id, handler);
    }

    [Fact]
    public async Task Get_AssemblesTheMonthWithRateAndSummary()
    {
        var c = await SeedAsync();

        var dash = (await c.Handler.GetAsync(c.MonthId, default))!;

        Assert.Equal((2026, 6, 4, new DateOnly(2026, 5, 28), new DateOnly(2026, 6, 24)), (dash.Month.Year, dash.Month.MonthNumber, dash.Month.WeekCount, dash.Month.Week1StartDate, dash.Month.LastDay));
        Assert.Equal((500m, "cache", false), (dash.ExchangeRate, dash.RateSource, dash.RateUnavailable));
        var s = dash.Summary!;
        Assert.Equal((1_500_000m, 3000m), (s.IncomeTotal.Crc, s.IncomeTotal.Usd));
        Assert.Equal((300_000m, 10_000m, 310_000m), (s.ExpensesAccount.Crc, s.ExpensesCard.Crc, s.ExpensesTotal.Crc));
        var mortgage = Assert.Single(s.FixedExpenses);
        Assert.Equal(("Mortgage", 350_000m, 700m, 300_000m), (mortgage.Name, mortgage.Budget.Crc, mortgage.Budget.Usd, mortgage.Actual.Crc));
        Assert.Equal(("Dining (old)", 10_000m), (Assert.Single(s.OtherSpending).CategoryName, s.OtherSpending[0].Actual.Crc)); // inactive category still named
        Assert.Equal(10_000m, s.UnplannedEssentialTotal.Crc);
        Assert.Equal(5_000m, s.RefundsTotal.Crc);
        Assert.Equal("Marchamo", Assert.Single(s.EnvelopeReminders).Name);
        Assert.Equal(4, s.WeeklyBudgeted.Count);
        Assert.Equal(300_000m, s.WeeklyBudgeted[1].Total.Crc);
        var bacAccount = Assert.Single(s.BankMethodBreakdown, b => b.PaymentMethod == "bank_account");
        Assert.Equal(("BAC", 350_000m, 300_000m), (bacAccount.BankName, bacAccount.Budget.Crc, bacAccount.Actual.Crc));
        Assert.Equal(1_500_000m - 310_000m, s.CurrentBalance.Crc);
    }

    [Fact]
    public async Task Get_NoRate_ReturnsTheMonthHeaderWithoutASummary()
    {
        var c = await SeedAsync(rate: null);

        var dash = (await c.Handler.GetAsync(c.MonthId, default))!;

        Assert.True(dash.RateUnavailable);
        Assert.Null(dash.Summary);
        Assert.Null(dash.ExchangeRate);
        Assert.Equal(c.MonthId, dash.Month.Id);
    }

    [Fact]
    public async Task Get_UnknownOrForeignMonth_IsNull()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();

        Assert.Null(await a.Handler.GetAsync(Guid.CreateVersion7(), default));
        Assert.Null(await a.Handler.GetAsync(b.MonthId, default)); // another household's month
    }
}
