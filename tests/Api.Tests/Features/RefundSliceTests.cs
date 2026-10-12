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
/// LEDGER-3 on real Postgres (ADR-V007/V014, donor US-012 + WU-2): a refund is derived from an
/// unplanned-essential transaction flagged with a percentage, follows every edit, dies with its
/// transaction; only its status is edited directly — <c>received</c> books a derived inflow (frozen rate,
/// source bank/category, <c>refund_realization</c>), <c>pending</c> removes it; a realized refund's
/// inflow tracks re-derived amounts; derived rows are read-only; two concurrent flips book exactly one
/// inflow. Fixture: June 2026 (4 weeks), rate 500.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RefundSliceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Jun20 = new(2026, 6, 20); // received in the purchase's own month (the pre-ADR-V017 shape)
    private static readonly DateOnly Jun5 = new(2026, 6, 5);

    private sealed class FixedRate : IExchangeRateResolver
    {
        public Task<ResolvedRate?> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ResolvedRate?>(new ResolvedRate(500m, RateSources.Live, T0));
    }

    public sealed record Ctx(AppDbContext Db, Guid Tenant, TransactionHandler Transactions, RefundHandler Refunds, Guid CategoryId, Guid BankId);

    private Ctx Build(AppDbContext db, Guid tenant, Guid categoryId, Guid bankId)
    {
        var current = new TestCurrentTenant { TenantId = tenant };
        var clock = new FakeTimeProvider(T0);
        var months = new MonthHandler(new EfRepository<Month>(db), new EfRepository<Week>(db), new EfRepository<Transaction>(db), new EfRepository<BudgetSettings>(db), new EfRepository<IncomeLine>(db), new EfRepository<MonthIncome>(db), new TenantRepository(db), new WeekBoundaryService(), current, clock);
        var transactions = new TransactionHandler(new EfRepository<Transaction>(db), new EfRepository<Refund>(db), new EfRepository<Category>(db), new EfRepository<Bank>(db), new EfRepository<Envelope>(db), new EfRepository<Card>(db), months, new FixedRate(), current, clock, NullLogger<TransactionHandler>.Instance);
        var refunds = new RefundHandler(new EfRepository<Refund>(db), new EfRepository<Transaction>(db), months, new EfUnitOfWork(db), current, clock, NullLogger<RefundHandler>.Instance);
        return new Ctx(db, tenant, transactions, refunds, categoryId, bankId);
    }

    private async Task<Ctx> ContextAsync()
    {
        var tenant = Guid.CreateVersion7();
        var db = Fixture.CreateContext(tenant);
        var category = new Category { TenantId = tenant, Name = "Health", CreatedAt = T0, UpdatedAt = T0 };
        var bank = new Bank { TenantId = tenant, Name = "Cash", CreatedAt = T0, UpdatedAt = T0 };
        db.Categories.Add(category); db.Banks.Add(bank);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return Build(db, tenant, category.Id, bank.Id);
    }

    /// <summary>A second, independent context/handler set on the same household — for concurrency.</summary>
    private Ctx Sibling(Ctx c) => Build(Fixture.CreateContext(c.Tenant), c.Tenant, c.CategoryId, c.BankId);

    /// <summary>A purchase; <paramref name="refundAmount"/> is in the purchase's own currency (#202: the amount is what is stored).</summary>
    private static CreateTransactionRequest Unplanned(Ctx c, bool refund = false, decimal? refundAmount = null, decimal amount = 50_000m, string type = "unplanned_essential", string currency = "CRC") =>
        new("Hospital", c.BankId, "credit_card", amount, currency, Jun5, c.CategoryId, type, 500m, null, refund, refundAmount);

    private static UpdateTransactionRequest Edit(Ctx c, decimal amount = 50_000m, string type = "unplanned_essential", bool refund = true, decimal? refundAmount = 25_000m, DateOnly? date = null, string currency = "CRC") =>
        new("Hospital", c.BankId, "credit_card", amount, currency, date ?? Jun5, c.CategoryId, type, null, refund, refundAmount);

    private async Task<Refund> TheRefund(Ctx c) => await c.Db.Refunds.SingleAsync();
    private async Task<List<Transaction>> Inflows(Ctx c) => await c.Db.Transactions.Where(t => t.TransactionType == "inflow").ToListAsync();

    // ---- LEDGER-4: the one field the household owns (the case number went with it on 2026-09-14 — it lives in the notes) ----

    [Fact]
    public async Task Details_AreTheHouseholdsOwn_AndSurviveTheTransactionBeingEdited()
    {
        // LEDGER-4: why the money is owed (claim number and all). Everything else on a refund is
        // derived and rewritten whenever its transaction changes — this one must not be.
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);
        var refund = await TheRefund(c);

        var (saved, error) = await c.Refunds.SetDetailsAsync(refund.Id, new UpdateRefundDetailsRequest("  CASE-2026-4471, lent to Diego  "), default);

        Assert.Null(error);
        Assert.Equal("CASE-2026-4471, lent to Diego", saved!.Notes); // trimmed

        // Rewrite: a new refund amount from the edit form rewrites the refund's money.
        var (updated, txError) = await c.Transactions.UpdateAsync(tx!.Id, Edit(c, amount: 80_000m, refundAmount: 40_000m), default);
        Assert.Null(txError);
        c.Db.ChangeTracker.Clear();

        var after = await c.Db.Refunds.AsNoTracking().SingleAsync();
        Assert.Equal(40_000m, after.AmountCrc);                                   // the derived half moved
        Assert.Equal("CASE-2026-4471, lent to Diego", after.Notes);               // the household's half did not
        Assert.NotNull(updated);
    }

    [Fact]
    public async Task Details_AreClearedByBlanks_AndRefusedWhenTooLong()
    {
        var c = await ContextAsync();
        await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);
        var refund = await TheRefund(c);
        await c.Refunds.SetDetailsAsync(refund.Id, new UpdateRefundDetailsRequest("note"), default);

        var (cleared, _) = await c.Refunds.SetDetailsAsync(refund.Id, new UpdateRefundDetailsRequest("   "), default);
        Assert.Null(cleared!.Notes); // blank clears, it does not "leave alone"

        var (_, notesTooLong) = await c.Refunds.SetDetailsAsync(refund.Id, new UpdateRefundDetailsRequest(new string('x', 251)), default);
        Assert.Equal("invalid_request", notesTooLong!.Error);
        Assert.Contains("notes", notesTooLong.Message);

        var (_, missing) = await c.Refunds.SetDetailsAsync(Guid.CreateVersion7(), new UpdateRefundDetailsRequest("x"), default);
        Assert.Equal("not_found", missing!.Error); // a foreign or unknown id is the uniform 404
    }

    // ---- derivation ----

    [Fact]
    public async Task Create_WithAnAmount_SpawnsAPendingRefund_OfThatAmount_AndNoStoredPercentage()
    {
        // #202 (owner, 2026-10-09): the amount is what is stored; a percentage is only the form's way of computing it.
        var c = await ContextAsync();

        var (tx, error) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 15_000m), default); // 50,000 CRC / 100 USD

        Assert.Null(error);
        Assert.True(tx!.RefundExpected);
        Assert.Equal((15_000m, "pending"), (tx.RefundAmount, tx.RefundStatus));
        var refund = await TheRefund(c);
        Assert.Equal(((decimal?)null, 15_000m, 30m, "pending", "Hospital", tx.MonthId, tx.Id), (refund.Percentage, refund.AmountCrc, refund.AmountUsd, refund.Status, refund.Payee, refund.MonthId, refund.TransactionId));
        Assert.Null(refund.InflowTransactionId);
    }

    [Fact]
    public async Task Create_InDollars_TheAmountIsInDollars_AndColonesFollowAtTheFrozenRate()
    {
        var c = await ContextAsync();

        var (tx, error) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 12.34m, amount: 80m, currency: "USD"), default);

        Assert.Null(error);
        Assert.Equal(12.34m, tx!.RefundAmount); // echoed in the purchase's currency
        var refund = await TheRefund(c);
        Assert.Equal((6_170m, 12.34m), (refund.AmountCrc, refund.AmountUsd)); // 12.34 x 500
    }

    [Fact]
    public async Task Create_TheWholeAmount_MatchesTheTransactionToTheCent()
    {
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 33_333.33m, amount: 33_333.33m), default);
        var refund = await TheRefund(c);
        Assert.Equal((tx!.AmountCrc, tx.AmountUsd), (refund.AmountCrc, refund.AmountUsd));
    }

    [Fact]
    public async Task Create_WithRefundNotes_RecordsThemOnTheRefund_AndAnEditReplacesThem()
    {
        // Owner, 2026-09-14: the reason (case number and all) is known while the receipt is in your hand, so the
        // form asks for it at entry rather than sending you to the month page afterwards. Same rules as the
        // details endpoint (LEDGER-4): trimmed, blank clears, 250 characters; null on an edit leaves it alone.
        var c = await ContextAsync();
        var (tx, error) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 15_000m) with { RefundNotes = "  CASE-7 · lent to Diego  " }, default);

        Assert.Null(error);
        Assert.Equal("CASE-7 · lent to Diego", tx!.RefundNotes); // echoed, so the edit form can prefill it
        Assert.Equal("CASE-7 · lent to Diego", (await TheRefund(c)).Notes);

        var (kept, keptError) = await c.Transactions.UpdateAsync(tx.Id, Edit(c, refundAmount: 15_000m), default); // no refund_notes sent
        Assert.Null(keptError);
        Assert.Equal("CASE-7 · lent to Diego", kept!.RefundNotes); // left alone

        var (edited, editError) = await c.Transactions.UpdateAsync(tx.Id, Edit(c, refundAmount: 15_000m) with { RefundNotes = "   " }, default);
        Assert.Null(editError);
        Assert.Null(edited!.RefundNotes);
        Assert.Null((await TheRefund(c)).Notes); // blank clears

        var (_, notesTooLong) = await c.Transactions.UpdateAsync(tx.Id, Edit(c, refundAmount: 15_000m) with { RefundNotes = new string('x', 251) }, default);
        Assert.Equal("invalid_request", notesTooLong!.Error);
        Assert.Contains("refund_notes", notesTooLong.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(50_000.01)] // more than the purchase itself
    public async Task Create_FlaggedWithoutAValidAmount_Is400_NothingSaved(double? amount)
    {
        var c = await ContextAsync();

        var (_, error) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: (decimal?)amount), default);

        Assert.Equal("invalid_request", error!.Error);
        Assert.Contains("refund_amount", error.Message);
        Assert.Equal(0, await c.Db.Transactions.CountAsync());
        Assert.Equal(0, await c.Db.Refunds.CountAsync());
    }

    [Fact]
    public async Task Create_DiscretionaryWithPercentage_SpawnsAPendingRefund()
    {
        // ADR-V025 (owner, 2026-10-09): a discretionary purchase you expect money back on stays discretionary —
        // the expectation is an attribute of the transaction, not a sixth class.
        var c = await ContextAsync();

        var (tx, error) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 15_000m, type: "extraordinary"), default);

        Assert.Null(error);
        Assert.True(tx!.RefundExpected);
        var refund = await TheRefund(c);
        Assert.Equal((15_000m, 30m, "pending", tx.Id), (refund.AmountCrc, refund.AmountUsd, refund.Status, refund.TransactionId));
    }

    [Theory]
    [InlineData("budgeted")]             // you don't budget for something you expect back (owner, 2026-10-09)
    [InlineData("inflow")]
    [InlineData("envelope_contribution")]
    public async Task Create_FlagOnAClassThatCannotCarryARefund_Is400_NothingSaved(string type)
    {
        // Fail closed (was: silently ignored) — a refund the household asked for never quietly disappears.
        var c = await ContextAsync();

        var (_, error) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 15_000m, type: type), default);

        Assert.Equal("invalid_request", error!.Error);
        Assert.Contains("refund_expected", error.Message);
        Assert.Equal(0, await c.Db.Transactions.CountAsync());
        Assert.Equal(0, await c.Db.Refunds.CountAsync());
    }

    [Fact]
    public async Task Create_ClassThatCannotCarryARefund_WithoutTheFlag_IsFine()
    {
        var c = await ContextAsync();
        var (_, error) = await c.Transactions.CreateAsync(Unplanned(c, type: "budgeted"), default);
        Assert.Null(error);
        Assert.Equal(0, await c.Db.Refunds.CountAsync());
    }

    [Fact]
    public async Task Create_UnplannedWithoutTheFlag_NoRefund()
    {
        var c = await ContextAsync();
        await c.Transactions.CreateAsync(Unplanned(c), default);
        Assert.Equal(0, await c.Db.Refunds.CountAsync());
    }

    // ---- the refund follows the transaction ----

    [Fact]
    public async Task Update_TransactionAmountChange_KeepsTheRefundAmount()
    {
        // #202: a refund is an amount, so a bigger purchase does not grow it — the form sends the stored amount back.
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);

        var (edited, error) = await c.Transactions.UpdateAsync(tx!.Id, Edit(c, amount: 80_000m, refundAmount: 25_000m), default);

        Assert.Null(error);
        Assert.Equal(25_000m, edited!.RefundAmount);
        var refund = await TheRefund(c);
        Assert.Equal((25_000m, 50m), (refund.AmountCrc, refund.AmountUsd));
    }

    [Fact]
    public async Task Update_TransactionBelowItsRefund_Is400_AndNothingChanges()
    {
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);
        c.Db.ChangeTracker.Clear();

        var (_, error) = await c.Transactions.UpdateAsync(tx!.Id, Edit(c, amount: 20_000m, refundAmount: 25_000m), default);

        Assert.Equal("invalid_request", error!.Error);
        Assert.Contains("refund_amount", error.Message);
        c.Db.ChangeTracker.Clear();
        Assert.Equal(50_000m, (await c.Db.Transactions.SingleAsync()).OriginalAmount);
    }

    [Fact]
    public async Task Update_ANewRefundAmount_ReplacesTheOld_AndClearsALegacyPercentage()
    {
        // A correction "by percentage" is the form computing a new amount from the current purchase; the server sees an
        // amount. A refund stored before #202 still carries its percentage until it is next written.
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);
        await c.Db.Refunds.ExecuteUpdateAsync(u => u.SetProperty(r => r.Percentage, 50m)); // the pre-#202 shape
        c.Db.ChangeTracker.Clear();

        await c.Transactions.UpdateAsync(tx!.Id, Edit(c, refundAmount: 10_000m), default);

        var refund = await TheRefund(c);
        Assert.Equal((10_000m, 20m, (decimal?)null), (refund.AmountCrc, refund.AmountUsd, refund.Percentage));
    }

    [Fact]
    public async Task Update_ClearingTheFlag_OrChangingClass_RemovesTheRefund()
    {
        var c = await ContextAsync();
        var (a, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);
        await c.Transactions.UpdateAsync(a!.Id, Edit(c, refund: false, refundAmount: null), default);
        Assert.Equal(0, await c.Db.Refunds.CountAsync());

        var (b, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);
        await c.Transactions.UpdateAsync(b!.Id, Edit(c, type: "budgeted", refund: false, refundAmount: null), default);
        Assert.Equal(0, await c.Db.Refunds.CountAsync());
    }

    [Fact]
    public async Task Update_UnplannedToDiscretionary_KeepsTheRefund()
    {
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);

        var (_, error) = await c.Transactions.UpdateAsync(tx!.Id, Edit(c, type: "extraordinary"), default);

        Assert.Null(error);
        Assert.Equal((25_000m, "pending"), ((await TheRefund(c)).AmountCrc, (await TheRefund(c)).Status));
    }

    [Fact]
    public async Task Update_ToBudgeted_StillFlagged_Is400_AndTheRefundStays()
    {
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);

        var (_, error) = await c.Transactions.UpdateAsync(tx!.Id, Edit(c, type: "budgeted"), default);

        Assert.Equal("invalid_request", error!.Error);
        Assert.Equal(1, await c.Db.Refunds.CountAsync());
    }

    [Fact]
    public async Task Update_DateMove_MovesTheRefundWithItsTransaction()
    {
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);

        var (moved, _) = await c.Transactions.UpdateAsync(tx!.Id, Edit(c, date: new DateOnly(2026, 7, 10)), default);

        Assert.Equal(moved!.MonthId, (await TheRefund(c)).MonthId);
        Assert.Equal(1, await c.Db.Months.CountAsync()); // June left with its last transaction
    }

    [Fact]
    public async Task Delete_Transaction_DeletesItsRefund_AndTheMonth()
    {
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);

        Assert.Null(await c.Transactions.DeleteAsync(tx!.Id, default));

        Assert.Equal(0, await c.Db.Refunds.CountAsync());
        Assert.Equal(0, await c.Db.Months.CountAsync());
    }

    // ---- status: the one directly editable field ----

    [Fact]
    public async Task MarkReceived_BooksADerivedInflow_WithTheSourcesRateBankAndCategory()
    {
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default); // 50,000 / 100 → 25,000 / 50
        var refundId = (await TheRefund(c)).Id;

        var (updated, error) = await c.Refunds.SetStatusAsync(refundId, new("Received", Jun20), default);

        Assert.Null(error);
        Assert.Equal("received", updated!.Status);
        var inflow = Assert.Single(await Inflows(c));
        Assert.Equal(updated.InflowTransactionId, inflow.Id);
        Assert.Equal((25_000m, 50m, 25_000m, "CRC", 500m, c.BankId, c.CategoryId, "refund_realization", tx!.MonthId, "Hospital"),
            (inflow.AmountCrc, inflow.AmountUsd, inflow.OriginalAmount, inflow.Currency, inflow.ExchangeRateUsed, inflow.BankId, inflow.CategoryId, inflow.Source, inflow.MonthId, inflow.Payee));
        Assert.Equal(inflow.Id, (await TheRefund(c)).InflowTransactionId);

        var rows = await c.Transactions.ListForMonthAsync(tx.MonthId, default);
        Assert.Equal(2, rows!.Count);
        Assert.Contains(rows, r => r.Source == "refund_realization" && r.TransactionType == "inflow");
    }

    [Fact]
    public async Task MarkReceived_WithADateInALaterMonth_BooksTheInflowThere_AndRevertRetiresThatMonth()
    {
        // ADR-V017: the refund stays in June (its purchase's month); the money landed in July, so the
        // inflow is dated July 3 and lives in July — auto-created like any transaction's month.
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);
        var refundId = (await TheRefund(c)).Id;
        var jul3 = new DateOnly(2026, 7, 3);

        var (updated, error) = await c.Refunds.SetStatusAsync(refundId, new("received", jul3), default);

        Assert.Null(error);
        Assert.Equal(("received", jul3), (updated!.Status, updated.ReceivedDate));
        var inflow = Assert.Single(await Inflows(c));
        Assert.Equal(jul3, inflow.TransactionDate);
        Assert.NotEqual(tx!.MonthId, inflow.MonthId);
        Assert.Equal(inflow.MonthId, updated.InflowMonthId);
        var july = await c.Db.Months.SingleAsync(m => m.Id == inflow.MonthId);
        Assert.Equal((2026, 7), (july.Year, july.MonthNumber));

        // The refund still lists under June, pointing at July; June's transactions hold only the purchase.
        var juneRefund = Assert.Single((await c.Refunds.ListForMonthAsync(tx.MonthId, default))!.Refunds);
        Assert.Equal((tx.MonthId, inflow.MonthId, jul3), (juneRefund.MonthId, juneRefund.InflowMonthId, juneRefund.ReceivedDate));
        Assert.Single((await c.Transactions.ListForMonthAsync(tx.MonthId, default))!);
        Assert.Single((await c.Transactions.ListForMonthAsync(inflow.MonthId, default))!, r => r.Source == "refund_realization");

        // Back to pending: the inflow goes, and July — now empty — with it; the received date clears.
        var (reverted, revertError) = await c.Refunds.SetStatusAsync(refundId, new("pending"), default);
        Assert.Null(revertError);
        Assert.Equal(("pending", (DateOnly?)null, (Guid?)null), (reverted!.Status, reverted.ReceivedDate, reverted.InflowMonthId));
        Assert.Empty(await Inflows(c));
        Assert.Null(await c.Db.Months.SingleOrDefaultAsync(m => m.Id == inflow.MonthId));
        Assert.NotNull(await c.Db.Months.SingleOrDefaultAsync(m => m.Id == tx.MonthId));
    }

    [Fact]
    public async Task MarkReceived_DefaultsToToday_AndRefusesADateBeforeThePurchase()
    {
        var c = await ContextAsync(); // clock = 2026-09-03; the purchase is June 5
        await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);
        var refundId = (await TheRefund(c)).Id;

        var early = await c.Refunds.SetStatusAsync(refundId, new("received", new DateOnly(2026, 6, 4)), default);
        Assert.Equal("invalid_request", early.Error!.Error);
        Assert.Empty(await Inflows(c));

        var (today, error) = await c.Refunds.SetStatusAsync(refundId, new("received"), default);
        Assert.Null(error);
        Assert.Equal(new DateOnly(2026, 9, 3), today!.ReceivedDate);
        var inflow = Assert.Single(await Inflows(c));
        Assert.Equal(new DateOnly(2026, 9, 3), inflow.TransactionDate);
        Assert.Equal((2026, 9), await c.Db.Months.Where(m => m.Id == inflow.MonthId).Select(m => new ValueTuple<int, int>(m.Year, m.MonthNumber)).SingleAsync());
    }

    [Fact]
    public async Task MarkReceived_Twice_IsIdempotent_OneInflow()
    {
        var c = await ContextAsync();
        await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);
        var refundId = (await TheRefund(c)).Id;

        await c.Refunds.SetStatusAsync(refundId, new("received", Jun20), default);
        var (again, error) = await c.Refunds.SetStatusAsync(refundId, new("received", Jun20), default);

        Assert.Null(error);
        Assert.Equal("received", again!.Status);
        Assert.Single(await Inflows(c));
    }

    [Fact]
    public async Task RevertToPending_RemovesTheInflow_KeepsTheSource()
    {
        var c = await ContextAsync();
        await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);
        var refundId = (await TheRefund(c)).Id;
        await c.Refunds.SetStatusAsync(refundId, new("received", Jun20), default);
        c.Db.ChangeTracker.Clear();

        var (reverted, error) = await c.Refunds.SetStatusAsync(refundId, new("pending", Jun20), default);

        Assert.Null(error);
        Assert.Equal(("pending", (Guid?)null), (reverted!.Status, reverted.InflowTransactionId));
        Assert.Empty(await Inflows(c));
        Assert.Equal(1, await c.Db.Transactions.CountAsync(t => t.TransactionType == "unplanned_essential"));
        Assert.Equal(1, await c.Db.Months.CountAsync());
    }

    [Fact]
    public async Task InvalidStatus_Is400_UnknownRefund_Is404()
    {
        var c = await ContextAsync();
        await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);
        var refundId = (await TheRefund(c)).Id;

        Assert.Equal("invalid_request", (await c.Refunds.SetStatusAsync(refundId, new("maybe"), default)).Error!.Error);
        Assert.Equal("not_found", (await c.Refunds.SetStatusAsync(Guid.CreateVersion7(), new("received", Jun20), default)).Error!.Error);
    }

    [Fact]
    public async Task DerivedInflow_IsReadOnlyThroughTheTransactionApi()
    {
        var c = await ContextAsync();
        await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);
        await c.Refunds.SetStatusAsync((await TheRefund(c)).Id, new("received", Jun20), default);
        var inflowId = Assert.Single(await Inflows(c)).Id;

        Assert.Equal("derived_transaction", (await c.Transactions.UpdateAsync(inflowId, Edit(c, type: "budgeted", refund: false, refundAmount: null), default)).Error!.Error);
        Assert.Equal("derived_transaction", (await c.Transactions.DeleteAsync(inflowId, default))!.Error);
        Assert.Single(await Inflows(c));
    }

    // ---- #202: a received refund is locked — "mark it pending first" ----

    private async Task<(Ctx C, TransactionResponse Tx)> ReceivedAsync()
    {
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);
        await c.Refunds.SetStatusAsync((await TheRefund(c)).Id, new("received", Jun20), default);
        c.Db.ChangeTracker.Clear();
        return (c, tx!);
    }

    private static async Task AssertUntouchedAsync(Ctx c)
    {
        c.Db.ChangeTracker.Clear();
        var refund = await c.Db.Refunds.SingleAsync();
        Assert.Equal((25_000m, "received"), (refund.AmountCrc, refund.Status));
        var inflow = Assert.Single(await c.Db.Transactions.Where(t => t.TransactionType == "inflow").ToListAsync());
        Assert.Equal((25_000m, 50m), (inflow.AmountCrc, inflow.AmountUsd));
        var source = await c.Db.Transactions.SingleAsync(t => t.TransactionType != "inflow");
        Assert.Equal((50_000m, "unplanned_essential"), (source.OriginalAmount, source.TransactionType));
    }

    public static TheoryData<string> LockedEdits => ["amount", "cleared", "budgeted"];

    [Theory]
    [MemberData(nameof(LockedEdits))]
    public async Task Update_TouchingAReceivedRefund_Is409_AndNothingChanges(string edit)
    {
        var (c, tx) = await ReceivedAsync();
        var request = edit switch
        {
            "amount" => Edit(c, refundAmount: 20_000m),                              // a new amount (or a new percentage)
            "cleared" => Edit(c, refund: false, refundAmount: null),                 // switching it off
            _ => Edit(c, type: "budgeted", refund: false, refundAmount: null),       // a class that cannot carry it
        };

        var (_, error) = await c.Transactions.UpdateAsync(tx.Id, request, default);

        Assert.Equal("refund_status_conflict", error!.Error);
        Assert.Contains("pending", error.Message);
        await AssertUntouchedAsync(c);
    }

    [Fact]
    public async Task Delete_SourceOfAReceivedRefund_Is409_AndNothingChanges()
    {
        var (c, tx) = await ReceivedAsync();

        var error = await c.Transactions.DeleteAsync(tx.Id, default);

        Assert.Equal("refund_status_conflict", error!.Error);
        await AssertUntouchedAsync(c);
    }

    [Fact]
    public async Task Update_AReceivedRefundsPurchase_WithoutTouchingTheRefund_Saves_AndNeverRewritesTheInflow()
    {
        var (c, tx) = await ReceivedAsync();

        var (edited, error) = await c.Transactions.UpdateAsync(tx.Id, Edit(c, amount: 80_000m, refundAmount: 25_000m, type: "extraordinary") with { Notes = "paid by the insurer" }, default);

        Assert.Null(error);
        Assert.Equal((80_000m, "received"), (edited!.OriginalAmount, edited.RefundStatus));
        c.Db.ChangeTracker.Clear();
        var inflow = Assert.Single(await Inflows(c));
        Assert.Equal((25_000m, 50m), (inflow.AmountCrc, inflow.AmountUsd));
    }

    [Fact]
    public async Task AReceivedRefund_PendingThenEditThenReceivedAgain_Works()
    {
        // The way out (owner-confirmed 2026-10-09): mark it pending, correct it, mark it received again.
        var (c, tx) = await ReceivedAsync();
        var refundId = (await TheRefund(c)).Id;

        Assert.Null((await c.Refunds.SetStatusAsync(refundId, new("pending"), default)).Error);
        c.Db.ChangeTracker.Clear();
        Assert.Null((await c.Transactions.UpdateAsync(tx.Id, Edit(c, refundAmount: 20_000m), default)).Error);
        c.Db.ChangeTracker.Clear();
        Assert.Null((await c.Refunds.SetStatusAsync(refundId, new("received", Jun20), default)).Error);

        c.Db.ChangeTracker.Clear();
        var inflow = Assert.Single(await Inflows(c));
        Assert.Equal((20_000m, 40m), (inflow.AmountCrc, inflow.AmountUsd));
    }

    // ---- list, tenancy, concurrency ----

    [Fact]
    public async Task ListForMonth_ReturnsTheMonthsRefunds_UnknownMonth_IsNull()
    {
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);

        var list = await c.Refunds.ListForMonthAsync(tx!.MonthId, default);

        var only = Assert.Single(list!.Refunds);
        Assert.Equal((25_000m, 0m, 25_000m), (list.Totals.Pending.Crc, list.Totals.Received.Crc, list.Totals.Expected.Crc)); // #206
        Assert.Equal(("Hospital", (decimal?)50m, "pending"), (only.Payee, only.Percentage, only.Status)); // computed: 25,000 of 50,000
        Assert.Null(await c.Refunds.ListForMonthAsync(Guid.CreateVersion7(), default));
    }

    // ---- #208: every refund of the household, across months ----

    private async Task<Ctx> TwoMonthsOfRefundsAsync()
    {
        // Hospital, June 5, ₡25,000 — received; Pharmacy, July 10, ₡10,000 — pending. The clock reads September 3.
        var c = await ContextAsync();
        await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);
        await c.Refunds.SetStatusAsync((await TheRefund(c)).Id, new("received", Jun20), default);
        await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 10_000m, amount: 40_000m, type: "extraordinary")
            with { Payee = "Farmacia Fischel", TransactionDate = new DateOnly(2026, 7, 10) }, default);
        c.Db.ChangeTracker.Clear();
        return c;
    }

    [Fact]
    public async Task List_AcrossMonths_OldestFirst_WithTheirMonth_PendingDays_AndTotals()
    {
        var c = await TwoMonthsOfRefundsAsync();

        var (list, error) = await c.Refunds.ListAsync(new RefundQuery(), default);

        Assert.Null(error);
        Assert.Equal(["Hospital", "Farmacia Fischel"], list!.Refunds.Select(r => r.Payee));
        var (hospital, pharmacy) = (list.Refunds[0], list.Refunds[1]);
        Assert.Equal(((int?)2026, (int?)6, (int?)null), (hospital.MonthYear, hospital.MonthNumber, hospital.PendingDays)); // received: not aging
        Assert.Equal(((int?)2026, (int?)7, (int?)55), (pharmacy.MonthYear, pharmacy.MonthNumber, pharmacy.PendingDays)); // July 10 → September 3
        Assert.Equal((10_000m, 25_000m, 35_000m), (list.Totals.Pending.Crc, list.Totals.Received.Crc, list.Totals.Expected.Crc));
        Assert.False(list.Truncated);
    }

    [Fact]
    public async Task List_FiltersByStatus_Payee_AndDates()
    {
        var c = await TwoMonthsOfRefundsAsync();
        async Task<string[]> Payees(RefundQuery q) => (await c.Refunds.ListAsync(q, default)).List!.Refunds.Select(r => r.Payee).ToArray();

        Assert.Equal(["Farmacia Fischel"], await Payees(new RefundQuery(Status: "pending")));
        Assert.Equal(["Hospital"], await Payees(new RefundQuery(Status: "RECEIVED")));
        Assert.Equal(["Hospital"], await Payees(new RefundQuery(Payee: "  hosp ")));          // case-insensitive, trimmed, contains
        Assert.Equal(["Farmacia Fischel"], await Payees(new RefundQuery(From: new DateOnly(2026, 7, 1))));
        Assert.Equal(["Hospital"], await Payees(new RefundQuery(To: new DateOnly(2026, 6, 30))));
        Assert.Empty(await Payees(new RefundQuery(Payee: "nobody")));

        // The totals follow the filter.
        var pending = (await c.Refunds.ListAsync(new RefundQuery(Status: "pending"), default)).List!;
        Assert.Equal((10_000m, 0m), (pending.Totals.Pending.Crc, pending.Totals.Received.Crc));
    }

    [Fact]
    public async Task List_RefusesABadStatus_OrABackwardsRange()
    {
        var c = await ContextAsync();
        Assert.Equal("invalid_request", (await c.Refunds.ListAsync(new RefundQuery(Status: "maybe"), default)).Error!.Error);
        Assert.Equal("invalid_request", (await c.Refunds.ListAsync(new RefundQuery(From: new DateOnly(2026, 7, 1), To: new DateOnly(2026, 6, 1)), default)).Error!.Error);
    }

    [Fact]
    public async Task List_IsTheCallersHouseholdOnly()
    {
        await TwoMonthsOfRefundsAsync();
        var b = await ContextAsync();
        Assert.Empty((await b.Refunds.ListAsync(new RefundQuery(), default)).List!.Refunds);
    }

    [Fact]
    public async Task Refunds_AreInvisibleAndUnflippable_AcrossTenants()
    {
        var a = await ContextAsync();
        var (tx, _) = await a.Transactions.CreateAsync(Unplanned(a, refund: true, refundAmount: 25_000m), default);
        var refundId = (await TheRefund(a)).Id;
        var b = await ContextAsync();

        Assert.Null(await b.Refunds.ListForMonthAsync(tx!.MonthId, default));
        Assert.Equal("not_found", (await b.Refunds.SetStatusAsync(refundId, new("received", Jun20), default)).Error!.Error);
        Assert.Equal("pending", (await TheRefund(a)).Status);
        Assert.Empty(await Inflows(a));
    }

    [Fact]
    public async Task ConcurrentMarkReceived_BooksExactlyOneInflow_TheLoserGetsAConflict()
    {
        var c = await ContextAsync();
        await c.Transactions.CreateAsync(Unplanned(c, refund: true, refundAmount: 25_000m), default);
        var refundId = (await TheRefund(c)).Id;
        var one = Sibling(c);
        var two = Sibling(c);

        var results = await Task.WhenAll(
            Task.Run(() => one.Refunds.SetStatusAsync(refundId, new("received", Jun20), default)),
            Task.Run(() => two.Refunds.SetStatusAsync(refundId, new("received", Jun20), default)));

        Assert.Equal(1, results.Count(r => r.Error is null));
        Assert.Equal("refund_status_conflict", Assert.Single(results, r => r.Error is not null).Error!.Error);
        await using var verify = Fixture.CreateContext(c.Tenant);
        Assert.Equal(1, await verify.Transactions.CountAsync(t => t.TransactionType == "inflow"));
        var refund = await verify.Refunds.SingleAsync(r => r.Id == refundId);
        Assert.Equal("received", refund.Status);
        Assert.NotNull(refund.InflowTransactionId);
    }
}
