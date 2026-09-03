using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Features.Ledger;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Features;

/// <summary>
/// LEDGER-1/2 on real Postgres (ADR-V005/V006/V007). Fixture: defaults (Thursday / last_weekday_prev)
/// → June 2026 = 4 weeks (May 28 – Jun 24), July 2026 = 5 weeks (Jun 25 – Jul 29). Proves the month
/// lifecycle (auto-create with weeks + income snapshot, reuse, resolve without writing, delete-last,
/// move-and-empty), the transaction rules (validation writes nothing, frozen rate, derived amounts,
/// envelope rules), uniform 404 across tenants, the contributor and the chain's last tier.
/// </summary>
[Collection(PostgresCollection.Name)]
public class LedgerSliceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Jun5 = new(2026, 6, 5);
    private static readonly DateOnly May30 = new(2026, 5, 30);   // still June's window
    private static readonly DateOnly Jul10 = new(2026, 7, 10);

    private sealed class FixedRate(decimal? rate) : IExchangeRateResolver
    {
        public Task<ResolvedRate?> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(rate is { } r ? new ResolvedRate(r, RateSources.Live, T0) : null);
    }

    public sealed record Ctx(AppDbContext Db, Guid Tenant, MonthHandler Months, TransactionHandler Transactions, Guid CategoryId, Guid BankId, Guid EnvelopeId);

    private async Task<Ctx> ContextAsync(Guid? tenantId = null, decimal? rate = 500m, bool withSettings = false)
    {
        var tenant = tenantId ?? Guid.CreateVersion7();
        var db = Fixture.CreateContext(tenant);
        var category = new Category { TenantId = tenant, Name = "Groceries", CreatedAt = T0, UpdatedAt = T0 };
        var bank = new Bank { TenantId = tenant, Name = "Cash", CreatedAt = T0, UpdatedAt = T0 };
        var envelope = new Envelope { TenantId = tenant, Name = "Marchamo", CreatedAt = T0, UpdatedAt = T0 };
        db.Categories.Add(category); db.Banks.Add(bank); db.Envelopes.Add(envelope);
        if (withSettings)
            db.BudgetSettings.Add(new BudgetSettings { TenantId = tenant, PrimaryIncome4w = 3000m, PrimaryIncome5w = 3750m, PrimaryIncomeCurrency = "USD", SecondaryIncome4w = 250_000m, SecondaryIncome5w = 312_500m, SecondaryIncomeCurrency = "CRC", CreatedAt = T0, UpdatedAt = T0 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var current = new TestCurrentTenant { TenantId = tenant };
        var clock = new FakeTimeProvider(T0);
        var months = new MonthHandler(new EfRepository<Month>(db), new EfRepository<Week>(db), new EfRepository<Transaction>(db), new EfRepository<BudgetSettings>(db), new WeekBoundaryService(), current, clock);
        var transactions = new TransactionHandler(new EfRepository<Transaction>(db), new EfRepository<Refund>(db), new EfRepository<Category>(db), new EfRepository<Bank>(db), new EfRepository<Envelope>(db), months, new FixedRate(rate), current, clock, NullLogger<TransactionHandler>.Instance);
        return new Ctx(db, tenant, months, transactions, category.Id, bank.Id, envelope.Id);
    }

    private static CreateTransactionRequest Create(Ctx c, DateOnly date, decimal amount = 50_000m, string currency = "CRC", string type = "budgeted", decimal? rate = null, string? method = null, Guid? envelope = null) =>
        new("AutoMercado", c.BankId, method, amount, currency, date, c.CategoryId, type, rate, envelope);

    private static UpdateTransactionRequest Update(Ctx c, DateOnly date, decimal amount = 50_000m, string currency = "CRC", string type = "budgeted", string payee = "AutoMercado") =>
        new(payee, c.BankId, "credit_card", amount, currency, date, c.CategoryId, type, null);

    // ---- LEDGER-1: months exist only through transactions ----

    [Fact]
    public async Task Create_UncoveredDate_AutoCreatesTheMonthWithWeeks_AndSnapshotsFiveWeekIncome()
    {
        var c = await ContextAsync(withSettings: true);

        var (tx, error) = await c.Transactions.CreateAsync(Create(c, Jul10), default);

        Assert.Null(error);
        var month = await c.Db.Months.SingleAsync();
        Assert.Equal((2026, 7, 5), (month.Year, month.MonthNumber, month.WeekCount));
        Assert.Equal(new DateOnly(2026, 6, 25), month.Week1StartDate);
        Assert.Equal(5, await c.Db.Weeks.CountAsync(w => w.MonthId == month.Id));
        Assert.Equal(new DateOnly(2026, 7, 29), await c.Db.Weeks.Where(w => w.MonthId == month.Id).MaxAsync(w => w.EndDate));
        Assert.Equal((3750m, "USD", 312_500m, "CRC"), (month.PrimaryIncomeAmount, month.PrimaryIncomeCurrency, month.SecondaryIncomeAmount, month.SecondaryIncomeCurrency));
        Assert.Equal(month.Id, tx!.MonthId);
    }

    [Fact]
    public async Task Create_FourWeekMonth_SnapshotsFourWeekIncome_AndDefaultsWhenNoSettingsRow()
    {
        var withSettings = await ContextAsync(withSettings: true);
        await withSettings.Transactions.CreateAsync(Create(withSettings, Jun5), default);
        var june = await withSettings.Db.Months.SingleAsync();
        Assert.Equal((6, 4, 3000m, 250_000m), (june.MonthNumber, june.WeekCount, june.PrimaryIncomeAmount, june.SecondaryIncomeAmount));
        Assert.Equal(new DateOnly(2026, 5, 28), june.Week1StartDate);

        var noSettings = await ContextAsync();
        await noSettings.Transactions.CreateAsync(Create(noSettings, Jun5), default);
        var defaults = await noSettings.Db.Months.SingleAsync();
        Assert.Equal((4, 0m, "USD"), (defaults.WeekCount, defaults.PrimaryIncomeAmount, defaults.PrimaryIncomeCurrency)); // BudgetSettings.Defaults
    }

    [Fact]
    public async Task Create_CoveredDate_ReusesTheMonth_EvenAcrossTheCalendarBoundary()
    {
        var c = await ContextAsync();
        await c.Transactions.CreateAsync(Create(c, Jun5), default);

        await c.Transactions.CreateAsync(Create(c, May30), default); // May 30 belongs to June's anchor window

        Assert.Equal(1, await c.Db.Months.CountAsync());
        Assert.Equal(2, await c.Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task Resolve_NeverWrites_AndNamesTheProspectiveMonth()
    {
        var c = await ContextAsync();

        var prospective = await c.Months.ResolveAsync(Jul10, default);
        Assert.Equal(new MonthResolveResponse(null, 2026, 7, IsNew: true), prospective);
        Assert.Equal(0, await c.Db.Months.CountAsync());

        await c.Transactions.CreateAsync(Create(c, Jun5), default);
        var existing = await c.Months.ResolveAsync(May30, default);
        Assert.False(existing!.IsNew);
        Assert.Equal((await c.Db.Months.SingleAsync()).Id, existing.MonthId);
    }

    [Fact]
    public async Task Create_RateUnresolvable_Is400_AndNothingIsWritten()
    {
        var c = await ContextAsync(rate: null);

        var (tx, error) = await c.Transactions.CreateAsync(Create(c, Jul10), default);

        Assert.Null(tx);
        Assert.Equal("exchange_rate_unavailable", error!.Error);
        Assert.Equal(0, await c.Db.Months.CountAsync());
        Assert.Equal(0, await c.Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task Create_ManualRateOverride_WinsOverTheChain()
    {
        var c = await ContextAsync(rate: null); // the chain has nothing; the override must still work

        var (tx, error) = await c.Transactions.CreateAsync(Create(c, Jun5, amount: 20m, currency: "usd", rate: 510m), default);

        Assert.Null(error);
        Assert.Equal((510m, 10_200m, 20m, "USD"), (tx!.ExchangeRateUsed, tx.AmountCrc, tx.AmountUsd, tx.Currency));
    }

    [Fact]
    public async Task Delete_LastTransaction_DeletesMonthAndWeeks_OthersKeepIt()
    {
        var c = await ContextAsync();
        var (first, _) = await c.Transactions.CreateAsync(Create(c, Jun5), default);
        var (second, _) = await c.Transactions.CreateAsync(Create(c, new DateOnly(2026, 6, 10)), default);

        Assert.Null(await c.Transactions.DeleteAsync(first!.Id, default));
        Assert.Equal(1, await c.Db.Months.CountAsync());

        Assert.Null(await c.Transactions.DeleteAsync(second!.Id, default));
        Assert.Equal(0, await c.Db.Months.CountAsync());
        Assert.Equal(0, await c.Db.Weeks.CountAsync());
        Assert.Equal("not_found", (await c.Transactions.DeleteAsync(second.Id, default))!.Error);
    }

    [Fact]
    public async Task Update_DateMove_CreatesTargetMonth_DeletesEmptiedSource_KeepsTheFrozenRate()
    {
        var c = await ContextAsync();
        var (created, _) = await c.Transactions.CreateAsync(Create(c, Jun5, rate: 500m), default);

        var (moved, error) = await c.Transactions.UpdateAsync(created!.Id, Update(c, Jul10, amount: 100_000m), default);

        Assert.Null(error);
        var month = await c.Db.Months.SingleAsync();
        Assert.Equal(7, month.MonthNumber);
        Assert.Equal(month.Id, moved!.MonthId);
        Assert.Equal(500m, moved.ExchangeRateUsed);          // frozen
        Assert.Equal((100_000m, 200m), (moved.AmountCrc, moved.AmountUsd)); // re-derived from the frozen rate
        Assert.Equal(0, await c.Db.Weeks.CountAsync(w => w.MonthId == created.MonthId));
    }

    [Fact]
    public async Task UpdateIncome_Edits_Validates_And404s()
    {
        var c = await ContextAsync();
        await c.Transactions.CreateAsync(Create(c, Jun5), default);
        var monthId = (await c.Db.Months.SingleAsync()).Id;

        var (updated, error) = await c.Months.UpdateIncomeAsync(monthId, new(1_600_000m, "crc", 700.004m, "USD"), default);
        Assert.Null(error);
        Assert.Equal((1_600_000m, "CRC", 700m, "USD"), (updated!.PrimaryIncomeAmount, updated.PrimaryIncomeCurrency, updated.SecondaryIncomeAmount, updated.SecondaryIncomeCurrency));

        Assert.Equal("invalid_request", (await c.Months.UpdateIncomeAsync(monthId, new(-1m, "USD", 0m, "USD"), default)).Error!.Error);
        Assert.Equal("invalid_request", (await c.Months.UpdateIncomeAsync(monthId, new(1m, "EUR", 0m, "USD"), default)).Error!.Error);
        Assert.Equal("not_found", (await c.Months.UpdateIncomeAsync(Guid.CreateVersion7(), new(1m, "USD", 0m, "USD"), default)).Error!.Error);
    }

    // ---- LEDGER-2: transaction rules ----

    public static TheoryData<Func<Ctx, CreateTransactionRequest>, string> Invalid => new()
    {
        { c => Create(c, Jun5) with { Payee = " " }, "payee" },
        { c => Create(c, Jun5, amount: 0m), "original_amount" },
        { c => Create(c, Jun5, currency: "EUR"), "currency" },
        { c => Create(c, Jun5) with { TransactionDate = null }, "transaction_date" },
        { c => Create(c, Jun5, type: "incidental"), "transaction_type" },
        { c => Create(c, Jun5, method: "cash"), "payment_method" },
        { c => Create(c, Jun5) with { BankId = null }, "bank_id" },
        { c => Create(c, Jun5) with { CategoryId = null }, "category_id" },
        { c => Create(c, Jun5) with { CategoryId = Guid.CreateVersion7() }, "category" },
        { c => Create(c, Jun5) with { BankId = Guid.CreateVersion7() }, "bank" },
        { c => Create(c, Jun5, rate: 0m), "exchange_rate" },
        { c => Create(c, Jun5, type: "envelope_contribution", method: "bank_account"), "envelope_id" },
        { c => Create(c, Jun5, type: "envelope_contribution", method: "credit_card", envelope: c.EnvelopeId), "bank_account" },
        { c => Create(c, Jun5, type: "budgeted", envelope: c.EnvelopeId), "envelope_id" },
        { c => Create(c, Jun5, type: "envelope_contribution", method: "bank_account", envelope: Guid.CreateVersion7()), "envelope" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public async Task Create_InvalidRequest_Is400_AndWritesNothing(Func<Ctx, CreateTransactionRequest> request, string messageMentions)
    {
        var c = await ContextAsync();

        var (tx, error) = await c.Transactions.CreateAsync(request(c), default);

        Assert.Null(tx);
        Assert.Equal("invalid_request", error!.Error);
        Assert.Contains(messageMentions, error.Message);
        Assert.Equal(0, await c.Db.Transactions.CountAsync());
        Assert.Equal(0, await c.Db.Months.CountAsync());
    }

    [Fact]
    public async Task Create_EnvelopeContribution_RequiresAnActiveEnvelope_AndBankAccount()
    {
        var c = await ContextAsync();

        var (tx, error) = await c.Transactions.CreateAsync(Create(c, Jun5, type: "Envelope_Contribution", method: "bank_account", envelope: c.EnvelopeId), default);

        Assert.Null(error);
        Assert.Equal(("envelope_contribution", "bank_account", c.EnvelopeId), (tx!.TransactionType, tx.PaymentMethod, tx.EnvelopeId));
    }

    [Fact]
    public async Task InactiveCatalogEntries_AreRefused_ButStillNameHistory()
    {
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Create(c, Jun5), default);
        var category = await c.Db.Categories.SingleAsync(x => x.Id == c.CategoryId);
        category.IsActive = false;
        await c.Db.SaveChangesAsync();

        var (_, error) = await c.Transactions.CreateAsync(Create(c, Jun5), default);
        Assert.Contains("inactive category", error!.Error == "invalid_request" ? error.Message : "");

        var rows = await c.Transactions.ListForMonthAsync(tx!.MonthId, default);
        Assert.Equal("Groceries", Assert.Single(rows!).CategoryName); // inactive names still label history (ADR-V008)
    }

    [Fact]
    public async Task ListForMonth_NewestFirst_WithNames_UnknownMonth_IsNull()
    {
        var c = await ContextAsync();
        await c.Transactions.CreateAsync(Create(c, Jun5) with { Payee = "older" }, default);
        await c.Transactions.CreateAsync(Create(c, new DateOnly(2026, 6, 20)) with { Payee = "newer" }, default);
        var monthId = (await c.Db.Months.SingleAsync()).Id;

        var rows = await c.Transactions.ListForMonthAsync(monthId, default);

        Assert.Equal(["newer", "older"], rows!.Select(r => r.Payee));
        Assert.All(rows!, r => Assert.Equal(("Groceries", "Cash", "manual"), (r.CategoryName, r.BankName, r.Source)));
        Assert.Null(await c.Transactions.ListForMonthAsync(Guid.CreateVersion7(), default));
    }

    [Fact]
    public async Task Ledger_IsInvisibleAndUnwritable_AcrossTenants()
    {
        var a = await ContextAsync();
        var (tx, _) = await a.Transactions.CreateAsync(Create(a, Jun5), default);
        var b = await ContextAsync();

        Assert.Empty((await b.Months.ListAsync(default))!);
        Assert.Null(await b.Months.GetAsync(tx!.MonthId, default));
        Assert.Null(await b.Transactions.GetAsync(tx.Id, default));
        Assert.Equal("not_found", (await b.Transactions.UpdateAsync(tx.Id, Update(b, Jun5, payee: "Hijacked"), default)).Error!.Error);
        Assert.Equal("not_found", (await b.Transactions.DeleteAsync(tx.Id, default))!.Error);
        Assert.Null(await b.Transactions.ListForMonthAsync(tx.MonthId, default));

        await using var verify = Fixture.CreateContext(a.Tenant);
        Assert.Equal("AutoMercado", (await verify.Transactions.SingleAsync(t => t.Id == tx.Id)).Payee);
    }

    [Fact]
    public async Task RecentRateSource_ReturnsTheLatestFrozenRate_OrNull()
    {
        var c = await ContextAsync();
        var source = new TransactionRecentRateSource(new EfRepository<Transaction>(c.Db));
        Assert.Null(await source.GetMostRecentAsync());

        await c.Transactions.CreateAsync(Create(c, Jun5, rate: 500m), default);
        await c.Transactions.CreateAsync(Create(c, Jun5, rate: 512m), default);

        Assert.Equal(512m, (await source.GetMostRecentAsync())!.Rate);
    }

    [Fact]
    public async Task Contributor_ReportsWipesAndExports_PerTenant()
    {
        var a = await ContextAsync();
        await a.Transactions.CreateAsync(Create(a, Jun5), default);
        var b = await ContextAsync();
        await b.Transactions.CreateAsync(Create(b, Jun5), default);

        var contributor = new LedgerDataContributor(new EfRepository<Month>(a.Db), new EfRepository<Week>(a.Db), new EfRepository<Transaction>(a.Db), new EfRepository<Refund>(a.Db));
        Assert.Equal("ledger", contributor.ExportKey);
        Assert.True(await contributor.HasDataAsync(a.Tenant));
        Assert.NotNull(await contributor.ExportAsync(a.Tenant));

        await contributor.WipeAsync(a.Tenant);

        Assert.False(await contributor.HasDataAsync(a.Tenant));
        Assert.Null(await contributor.ExportAsync(a.Tenant));
        Assert.True(await contributor.HasDataAsync(b.Tenant)); // the other household keeps its ledger
    }
}
