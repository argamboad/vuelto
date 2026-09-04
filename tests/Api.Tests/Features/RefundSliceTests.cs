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
        var months = new MonthHandler(new EfRepository<Month>(db), new EfRepository<Week>(db), new EfRepository<Transaction>(db), new EfRepository<BudgetSettings>(db), new WeekBoundaryService(), current, clock);
        var transactions = new TransactionHandler(new EfRepository<Transaction>(db), new EfRepository<Refund>(db), new EfRepository<Category>(db), new EfRepository<Bank>(db), new EfRepository<Envelope>(db), months, new FixedRate(), current, clock, NullLogger<TransactionHandler>.Instance);
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

    private static CreateTransactionRequest Unplanned(Ctx c, bool refund = false, decimal? pct = null, decimal amount = 50_000m, string type = "unplanned_essential") =>
        new("Hospital", c.BankId, "credit_card", amount, "CRC", Jun5, c.CategoryId, type, 500m, null, refund, pct);

    private static UpdateTransactionRequest Edit(Ctx c, decimal amount = 50_000m, string type = "unplanned_essential", bool refund = true, decimal? pct = 50m, DateOnly? date = null) =>
        new("Hospital", c.BankId, "credit_card", amount, "CRC", date ?? Jun5, c.CategoryId, type, null, refund, pct);

    private async Task<Refund> TheRefund(Ctx c) => await c.Db.Refunds.SingleAsync();
    private async Task<List<Transaction>> Inflows(Ctx c) => await c.Db.Transactions.Where(t => t.TransactionType == "inflow").ToListAsync();

    // ---- derivation ----

    [Fact]
    public async Task Create_UnplannedWithPercentage_SpawnsAPendingRefund_OfThatPercentage()
    {
        var c = await ContextAsync();

        var (tx, error) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, pct: 30m), default); // 50,000 CRC / 100 USD

        Assert.Null(error);
        Assert.True(tx!.RefundExpected);
        Assert.Equal(30m, tx.RefundPercentage);
        var refund = await TheRefund(c);
        Assert.Equal((30m, 15_000m, 30m, "pending", "Hospital", tx.MonthId, tx.Id), (refund.Percentage, refund.AmountCrc, refund.AmountUsd, refund.Status, refund.Payee, refund.MonthId, refund.TransactionId));
        Assert.Null(refund.InflowTransactionId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(150.0)]
    public async Task Create_FlaggedWithoutAValidPercentage_Is400_NothingSaved(double? pct)
    {
        var c = await ContextAsync();

        var (_, error) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, pct: (decimal?)pct), default);

        Assert.Equal("invalid_request", error!.Error);
        Assert.Contains("refund_percentage", error.Message);
        Assert.Equal(0, await c.Db.Transactions.CountAsync());
        Assert.Equal(0, await c.Db.Refunds.CountAsync());
    }

    [Theory]
    [InlineData("extraordinary")]
    [InlineData("inflow")]
    [InlineData("budgeted")]
    public async Task Create_FlagOnAnotherClass_IsIgnored_NoRefund(string type)
    {
        var c = await ContextAsync();

        var (tx, error) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, pct: 30m, type: type), default);

        Assert.Null(error);
        Assert.False(tx!.RefundExpected);
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
    public async Task Update_AmountChange_RederivesTheRefund()
    {
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, pct: 50m), default);

        await c.Transactions.UpdateAsync(tx!.Id, Edit(c, amount: 80_000m), default);

        var refund = await TheRefund(c);
        Assert.Equal((40_000m, 80m), (refund.AmountCrc, refund.AmountUsd)); // 80,000 × 50% at the frozen 500
    }

    [Fact]
    public async Task Update_ClearingTheFlag_OrChangingClass_RemovesTheRefund()
    {
        var c = await ContextAsync();
        var (a, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, pct: 50m), default);
        await c.Transactions.UpdateAsync(a!.Id, Edit(c, refund: false, pct: null), default);
        Assert.Equal(0, await c.Db.Refunds.CountAsync());

        var (b, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, pct: 50m), default);
        await c.Transactions.UpdateAsync(b!.Id, Edit(c, type: "budgeted"), default);
        Assert.Equal(0, await c.Db.Refunds.CountAsync());
    }

    [Fact]
    public async Task Update_DateMove_MovesTheRefundWithItsTransaction()
    {
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, pct: 50m), default);

        var (moved, _) = await c.Transactions.UpdateAsync(tx!.Id, Edit(c, date: new DateOnly(2026, 7, 10)), default);

        Assert.Equal(moved!.MonthId, (await TheRefund(c)).MonthId);
        Assert.Equal(1, await c.Db.Months.CountAsync()); // June left with its last transaction
    }

    [Fact]
    public async Task Delete_Transaction_DeletesItsRefund_AndTheMonth()
    {
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, pct: 50m), default);

        Assert.Null(await c.Transactions.DeleteAsync(tx!.Id, default));

        Assert.Equal(0, await c.Db.Refunds.CountAsync());
        Assert.Equal(0, await c.Db.Months.CountAsync());
    }

    // ---- status: the one directly editable field ----

    [Fact]
    public async Task MarkReceived_BooksADerivedInflow_WithTheSourcesRateBankAndCategory()
    {
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, pct: 50m), default); // 50,000 / 100 → 25,000 / 50
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
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, pct: 50m), default);
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
        var juneRefund = Assert.Single((await c.Refunds.ListForMonthAsync(tx.MonthId, default))!);
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
        await c.Transactions.CreateAsync(Unplanned(c, refund: true, pct: 50m), default);
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
        await c.Transactions.CreateAsync(Unplanned(c, refund: true, pct: 50m), default);
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
        await c.Transactions.CreateAsync(Unplanned(c, refund: true, pct: 50m), default);
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
        await c.Transactions.CreateAsync(Unplanned(c, refund: true, pct: 50m), default);
        var refundId = (await TheRefund(c)).Id;

        Assert.Equal("invalid_request", (await c.Refunds.SetStatusAsync(refundId, new("maybe"), default)).Error!.Error);
        Assert.Equal("not_found", (await c.Refunds.SetStatusAsync(Guid.CreateVersion7(), new("received", Jun20), default)).Error!.Error);
    }

    [Fact]
    public async Task DerivedInflow_IsReadOnlyThroughTheTransactionApi()
    {
        var c = await ContextAsync();
        await c.Transactions.CreateAsync(Unplanned(c, refund: true, pct: 50m), default);
        await c.Refunds.SetStatusAsync((await TheRefund(c)).Id, new("received", Jun20), default);
        var inflowId = Assert.Single(await Inflows(c)).Id;

        Assert.Equal("derived_transaction", (await c.Transactions.UpdateAsync(inflowId, Edit(c, type: "budgeted", refund: false, pct: null), default)).Error!.Error);
        Assert.Equal("derived_transaction", (await c.Transactions.DeleteAsync(inflowId, default))!.Error);
        Assert.Single(await Inflows(c));
    }

    [Fact]
    public async Task Delete_SourceWithRealizedRefund_RemovesRefundInflowAndMonth()
    {
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, pct: 50m), default);
        await c.Refunds.SetStatusAsync((await TheRefund(c)).Id, new("received", Jun20), default);
        Assert.Equal(2, await c.Db.Transactions.CountAsync());
        c.Db.ChangeTracker.Clear();

        Assert.Null(await c.Transactions.DeleteAsync(tx!.Id, default));

        Assert.Equal(0, await c.Db.Transactions.CountAsync());
        Assert.Equal(0, await c.Db.Refunds.CountAsync());
        Assert.Equal(0, await c.Db.Months.CountAsync());
    }

    [Fact]
    public async Task Update_SourceAfterReceived_InflowTracksTheRederivedAmounts()
    {
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, pct: 50m), default);
        await c.Refunds.SetStatusAsync((await TheRefund(c)).Id, new("received", Jun20), default);
        c.Db.ChangeTracker.Clear();

        await c.Transactions.UpdateAsync(tx!.Id, Edit(c, amount: 80_000m), default); // refund → 40,000 / 80

        Assert.Equal(40_000m, (await TheRefund(c)).AmountCrc);
        var inflow = Assert.Single(await Inflows(c));
        Assert.Equal((40_000m, 80m, 40_000m), (inflow.AmountCrc, inflow.AmountUsd, inflow.OriginalAmount));
    }

    [Fact]
    public async Task Update_ClearingTheFlagAfterReceived_RemovesTheRealizedInflow()
    {
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, pct: 50m), default);
        await c.Refunds.SetStatusAsync((await TheRefund(c)).Id, new("received", Jun20), default);
        c.Db.ChangeTracker.Clear();

        await c.Transactions.UpdateAsync(tx!.Id, Edit(c, refund: false, pct: null), default);

        Assert.Equal(0, await c.Db.Refunds.CountAsync());
        Assert.Empty(await Inflows(c));
        Assert.Equal(1, await c.Db.Transactions.CountAsync());
    }

    // ---- list, tenancy, concurrency ----

    [Fact]
    public async Task ListForMonth_ReturnsTheMonthsRefunds_UnknownMonth_IsNull()
    {
        var c = await ContextAsync();
        var (tx, _) = await c.Transactions.CreateAsync(Unplanned(c, refund: true, pct: 50m), default);

        var list = await c.Refunds.ListForMonthAsync(tx!.MonthId, default);

        var only = Assert.Single(list!);
        Assert.Equal(("Hospital", 50m, "pending"), (only.Payee, only.Percentage, only.Status));
        Assert.Null(await c.Refunds.ListForMonthAsync(Guid.CreateVersion7(), default));
    }

    [Fact]
    public async Task Refunds_AreInvisibleAndUnflippable_AcrossTenants()
    {
        var a = await ContextAsync();
        var (tx, _) = await a.Transactions.CreateAsync(Unplanned(a, refund: true, pct: 50m), default);
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
        await c.Transactions.CreateAsync(Unplanned(c, refund: true, pct: 50m), default);
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
