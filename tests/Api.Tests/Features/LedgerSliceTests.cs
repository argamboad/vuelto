using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Features.Cards;
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

    /// <summary>ADR-V019: a resolver that serves the day's buy/sell pair (the BCCR provider tiers).</summary>
    private sealed class PairRate(FxRates rates) : IExchangeRateResolver
    {
        public Task<ResolvedRate?> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ResolvedRate?>(new ResolvedRate(rates, RateSources.Live, T0));
    }

    public sealed record Ctx(AppDbContext Db, Guid Tenant, MonthHandler Months, TransactionHandler Transactions, Guid CategoryId, Guid BankId, Guid EnvelopeId);

    /// <summary>
    /// <paramref name="withIncome"/> seeds the household's income lines (INCOME-1): $750 a week, ₡62,500 a week, a monthly
    /// line whose member has left (never snapshotted) and an inactive one.
    /// </summary>
    private async Task<Ctx> ContextAsync(Guid? tenantId = null, decimal? rate = 500m, bool withIncome = false, FxRates? pair = null)
    {
        var tenant = tenantId ?? Guid.CreateVersion7();
        var db = Fixture.CreateContext(tenant);
        var category = new Category { TenantId = tenant, Name = "Groceries", CreatedAt = T0, UpdatedAt = T0 };
        var bank = new Bank { TenantId = tenant, Name = "Cash", CreatedAt = T0, UpdatedAt = T0 };
        var envelope = new Envelope { TenantId = tenant, Name = "Marchamo", CreatedAt = T0, UpdatedAt = T0 };
        db.Categories.Add(category); db.Banks.Add(bank); db.Envelopes.Add(envelope);
        if (withIncome)
            db.AddRange(
                new IncomeLine { TenantId = tenant, Name = "Salary", Currency = "USD", PayPeriod = PayPeriods.Weekly, Amount = 750m, SortOrder = 0, CreatedAt = T0, UpdatedAt = T0 },
                new IncomeLine { TenantId = tenant, Name = "Side job", Currency = "CRC", PayPeriod = PayPeriods.Weekly, Amount = 62_500m, SortOrder = 1, CreatedAt = T0, UpdatedAt = T0 },
                new IncomeLine { TenantId = tenant, Name = "Left the house", MemberUserId = Guid.CreateVersion7(), Currency = "USD", PayPeriod = PayPeriods.Monthly, Amount = 900m, SortOrder = 2, CreatedAt = T0, UpdatedAt = T0 },
                new IncomeLine { TenantId = tenant, Name = "Old job", Currency = "USD", PayPeriod = PayPeriods.Monthly, Amount = 100m, IsActive = false, SortOrder = 3, CreatedAt = T0, UpdatedAt = T0 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var current = new TestCurrentTenant { TenantId = tenant };
        var clock = new FakeTimeProvider(T0);
        var months = new MonthHandler(new EfRepository<Month>(db), new EfRepository<Week>(db), new EfRepository<Transaction>(db), new EfRepository<BudgetSettings>(db), new EfRepository<IncomeLine>(db), new EfRepository<MonthIncome>(db), new TenantRepository(db), new WeekBoundaryService(), current, clock);
        var transactions = new TransactionHandler(new EfRepository<Transaction>(db), new EfRepository<Refund>(db), new EfRepository<Category>(db), new EfRepository<Bank>(db), new EfRepository<Envelope>(db), new EfRepository<Card>(db), months, pair is null ? new FixedRate(rate) : new PairRate(pair), current, clock, NullLogger<TransactionHandler>.Instance);
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
        var c = await ContextAsync(withIncome: true);

        var (tx, error) = await c.Transactions.CreateAsync(Create(c, Jul10), default);

        Assert.Null(error);
        var month = await c.Db.Months.SingleAsync();
        Assert.Equal((2026, 7, 5), (month.Year, month.MonthNumber, month.WeekCount));
        Assert.Equal(new DateOnly(2026, 6, 25), month.Week1StartDate);
        Assert.Equal(5, await c.Db.Weeks.CountAsync(w => w.MonthId == month.Id));
        Assert.Equal(new DateOnly(2026, 7, 29), await c.Db.Weeks.Where(w => w.MonthId == month.Id).MaxAsync(w => w.EndDate));
        var income = await c.Db.MonthIncomes.Where(r => r.MonthId == month.Id).OrderBy(r => r.SortOrder).ToListAsync();
        Assert.Equal(["Salary", "Side job"], income.Select(r => r.Label)); // the departed member's and the inactive line don't snapshot
        Assert.Equal((3750m, 3750m, "USD"), (income[0].Amount, income[0].PlannedAmount!.Value, income[0].Currency)); // $750 × 5 weeks
        Assert.Equal((312_500m, "CRC"), (income[1].Amount, income[1].Currency));
        Assert.Equal(month.Id, tx!.MonthId);
    }

    [Fact]
    public async Task Create_FourWeekMonth_SnapshotsFourWeekIncome_AndDefaultsWhenNoSettingsRow()
    {
        var withIncome = await ContextAsync(withIncome: true);
        await withIncome.Transactions.CreateAsync(Create(withIncome, Jun5), default);
        var june = await withIncome.Db.Months.SingleAsync();
        Assert.Equal((6, 4), (june.MonthNumber, june.WeekCount));
        Assert.Equal([3000m, 250_000m], await withIncome.Db.MonthIncomes.Where(r => r.MonthId == june.Id).OrderBy(r => r.SortOrder).Select(r => r.Amount).ToListAsync());
        Assert.Equal(new DateOnly(2026, 5, 28), june.Week1StartDate);

        var noLines = await ContextAsync();
        await noLines.Transactions.CreateAsync(Create(noLines, Jun5), default);
        var bare = await noLines.Db.Months.SingleAsync();
        Assert.Equal(4, bare.WeekCount); // BudgetSettings.Defaults
        Assert.Equal(0, await noLines.Db.MonthIncomes.CountAsync()); // no lines, no income rows — never seeded
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

        // Jul 10 falls in the third week of July's window (Thu Jun 25 -> Jul 1, Jul 2 -> 8, Jul 9 -> 15): the week is
        // named even for a month that does not exist yet, from the same boundaries that would create it.
        var prospective = await c.Months.ResolveAsync(Jul10, default);
        Assert.Equal(new MonthResolveResponse(null, 2026, 7, IsNew: true, WeekNumber: 3), prospective);
        Assert.Equal(0, await c.Db.Months.CountAsync());

        await c.Transactions.CreateAsync(Create(c, Jun5), default);
        var existing = await c.Months.ResolveAsync(May30, default);
        Assert.False(existing!.IsNew);
        Assert.Equal((await c.Db.Months.SingleAsync()).Id, existing.MonthId);
        Assert.Equal(1, existing.WeekNumber); // May 30 sits in June's first stored week (May 28 -> Jun 3)
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
    public async Task Create_FreezesTheSideForTheCurrency_DollarsAtSell_ColonesAtBuy()
    {
        // ADR-V019: the same rule the voucher confirm follows — spending converts at the rate you would pay to fund it.
        var c = await ContextAsync(pair: new FxRates(Buy: 448.27m, Sell: 453.69m));

        var (usd, e1) = await c.Transactions.CreateAsync(Create(c, Jun5, amount: 20m, currency: "USD"), default);
        var (crc, e2) = await c.Transactions.CreateAsync(Create(c, Jun5, amount: 50_000m, currency: "CRC"), default);

        Assert.Null(e1); Assert.Null(e2);
        Assert.Equal((453.69m, 9073.80m, 20m), (usd!.ExchangeRateUsed, usd.AmountCrc, usd.AmountUsd));      // $20 × venta
        Assert.Equal((448.27m, 50_000m, 111.54m), (crc!.ExchangeRateUsed, crc.AmountCrc, crc.AmountUsd));   // ₡50,000 / compra
    }

    [Fact]
    public async Task Notes_AreOptional_Trimmed_ClearedWhenBlank_AndCappedAt250()
    {
        // "Why did we spend this?" — a short note on the row (2026-09-08). Blank means none; over 250 is a 400, never a silent cut.
        var c = await ContextAsync();

        var (tx, error) = await c.Transactions.CreateAsync(Create(c, Jun5) with { Notes = "  Tuti's birthday dinner  " }, default);
        Assert.Null(error);
        Assert.Equal("Tuti's birthday dinner", tx!.Notes);
        Assert.Equal("Tuti's birthday dinner", (await c.Db.Transactions.SingleAsync()).Notes);

        var (_, tooLong) = await c.Transactions.CreateAsync(Create(c, Jun5) with { Notes = new string('x', 251) }, default);
        Assert.Equal("invalid_request", tooLong!.Error);
        Assert.Contains("notes", tooLong.Message);

        var (updated, e2) = await c.Transactions.UpdateAsync(tx.Id, Update(c, Jun5) with { Notes = "   " }, default);
        Assert.Null(e2);
        Assert.Null(updated!.Notes); // whitespace clears the note

        var rows = await c.Transactions.ListForMonthAsync(tx.MonthId, default);
        Assert.Null(Assert.Single(rows!).Notes);
    }

    [Fact]
    public async Task Create_WithACard_LinksIt_ListsItsAlias_AndRefusesAForeignOrInactiveOne()
    {
        // CARDS-1: the card is optional; when given it must be the household's and active — like the bank.
        var c = await ContextAsync();
        var cards = new CardHandler(new EfRepository<Card>(c.Db), new EfRepository<Vuelto.Core.Entities.CardIdentity>(c.Db), new EfRepository<Transaction>(c.Db), new EfRepository<Bank>(c.Db), new TestCurrentTenant { TenantId = c.Tenant }, new FakeTimeProvider(T0));
        var card = (await cards.CreateAsync(new CreateCardRequest("Main", "VISA", "1234", c.BankId), default)).Card!;

        var (tx, error) = await c.Transactions.CreateAsync(Create(c, Jun5) with { CardId = card.Id }, default);
        Assert.Null(error);
        Assert.Equal(card.Id, tx!.CardId);
        var row = Assert.Single((await c.Transactions.ListForMonthAsync(tx.MonthId, default))!);
        Assert.Equal("Main", row.CardName);

        var (_, foreign) = await c.Transactions.CreateAsync(Create(c, Jun5) with { CardId = Guid.CreateVersion7() }, default);
        Assert.Equal("invalid_request", foreign!.Error);
        Assert.Contains("card", foreign.Message);

        await cards.UpdateAsync(card.Id, new UpdateCardRequest("Main", c.BankId, IsActive: false), default);
        var (_, inactive) = await c.Transactions.CreateAsync(Create(c, Jun5) with { CardId = card.Id }, default);
        Assert.Contains("inactive card", inactive!.Message);
        Assert.Equal("Main", Assert.Single((await c.Transactions.ListForMonthAsync(tx.MonthId, default))!).CardName); // history keeps the alias
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

        static UpdateMonthIncomeRequest Rows(params MonthIncomeRowRequest[] rows) => new([.. rows]);
        static MonthIncomeRowRequest Row(Guid? id, string? label, Guid? member, string? currency, decimal amount) => new(id, label, member, currency, amount);

        var (updated, error) = await c.Months.UpdateIncomeAsync(monthId, Rows(Row(null, " Salary ", null, "crc", 1_600_000m), Row(null, "Bonus", null, "USD", 700.004m)), default);
        Assert.Null(error);
        Assert.Equal([("Salary", "CRC", 1_600_000m, (decimal?)null), ("Bonus", "USD", 700m, null)],
            updated!.IncomeRows!.Select(r => (r.Label, r.Currency, r.Amount, r.PlannedAmount)));

        Assert.Equal("invalid_request", (await c.Months.UpdateIncomeAsync(monthId, Rows(Row(null, "x", null, "USD", -1m)), default)).Error!.Error);
        Assert.Equal("invalid_request", (await c.Months.UpdateIncomeAsync(monthId, Rows(Row(null, "x", null, "EUR", 1m)), default)).Error!.Error);
        Assert.Equal("invalid_request", (await c.Months.UpdateIncomeAsync(monthId, Rows(Row(null, " ", null, "USD", 1m)), default)).Error!.Error);
        Assert.Equal("invalid_request", (await c.Months.UpdateIncomeAsync(monthId, Rows(Row(null, "x", Guid.CreateVersion7(), "USD", 1m)), default)).Error!.Error);
        Assert.Equal("invalid_request", (await c.Months.UpdateIncomeAsync(monthId, Rows(Row(Guid.CreateVersion7(), "x", null, "USD", 1m)), default)).Error!.Error);
        Assert.Equal("invalid_request", (await c.Months.UpdateIncomeAsync(monthId, new(null), default)).Error!.Error);
        Assert.Equal("not_found", (await c.Months.UpdateIncomeAsync(Guid.CreateVersion7(), Rows(), default)).Error!.Error);
    }

    [Fact]
    public async Task UpdateIncome_KeepsThePlanOnEditedRows_AddsOneOffs_AndRemovesWhatIsLeftOut()
    {
        var c = await ContextAsync(withIncome: true);
        await c.Transactions.CreateAsync(Create(c, Jun5), default);
        var monthId = (await c.Db.Months.SingleAsync()).Id;
        var month = (await c.Months.GetAsync(monthId, default))!;
        var salary = month.IncomeRows!.Single(r => r.Label == "Salary");

        var (updated, error) = await c.Months.UpdateIncomeAsync(monthId, new(
        [
            new(null, "Sold the bike", null, "CRC", 150_000m),
            new(salary.Id, "Salary (short week)", null, "USD", 2_800m),
            // "Side job" is left out → removed
        ]), default);

        Assert.Null(error);
        var rows = updated!.IncomeRows!;
        Assert.Equal(["Sold the bike", "Salary (short week)"], rows.Select(r => r.Label)); // request order
        Assert.Equal((salary.Id, salary.IncomeLineId, 2_800m, (decimal?)3_000m), (rows[1].Id, rows[1].IncomeLineId, rows[1].Amount, rows[1].PlannedAmount));
        Assert.Null(rows[0].IncomeLineId);
        Assert.Null(rows[0].PlannedAmount);
        Assert.Equal(2, await c.Db.MonthIncomes.CountAsync(r => r.MonthId == monthId));

        // The same row twice is refused and nothing changes.
        var (_, repeated) = await c.Months.UpdateIncomeAsync(monthId, new([new(salary.Id, "a", null, "USD", 1m), new(salary.Id, "b", null, "USD", 1m)]), default);
        Assert.Equal("invalid_request", repeated!.Error);
        Assert.Equal(2_800m, (await c.Db.MonthIncomes.AsNoTracking().SingleAsync(r => r.Id == salary.Id)).Amount);
    }

    [Fact]
    public async Task EmptyingAMonth_TakesItsIncomeRowsWithIt()
    {
        var c = await ContextAsync(withIncome: true);
        var (tx, _) = await c.Transactions.CreateAsync(Create(c, Jun5), default);
        Assert.Equal(2, await c.Db.MonthIncomes.CountAsync());

        Assert.Null(await c.Transactions.DeleteAsync(tx!.Id, default));

        Assert.Equal(0, await c.Db.Months.CountAsync());
        Assert.Equal(0, await c.Db.MonthIncomes.CountAsync());
        Assert.Equal(4, await c.Db.IncomeLines.CountAsync()); // the lines stay
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
