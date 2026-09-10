using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Features.Cards;
using Vuelto.Api.Features.Email;
using Vuelto.Api.Features.Ledger;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Vouchers;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Features;

/// <summary>
/// EMAIL-6 on real Postgres (donor US-030/US-033 + WU-3 A6): the queue lists pending drafts only; confirm books
/// an <c>email</c> transaction through the SAME create as manual entry (month auto-created, live rate frozen)
/// and flips the draft in one boundary; a validation or rate failure writes nothing and the draft stays
/// pending; two concurrent confirms book exactly one transaction (the loser's rolls back → <c>not_pending</c>);
/// discard is a guarded flip that never reverts a confirmed draft; the tombstone outlives both; overrides
/// apply; learn-on-confirm remembers once; foreign ids are a uniform 404.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PendingVoucherSliceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Jun13 = new(2026, 6, 13);

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

    private sealed record Ctx(AppDbContext Db, Guid Tenant, PendingVoucherHandler Handler, MerchantMappingHandler Mappings, Guid CategoryId, Guid BankId);

    private async Task<Ctx> ContextAsync(decimal? rate = 500m, FxRates? pair = null)
    {
        var tenant = Guid.CreateVersion7();
        var db = Fixture.CreateContext(tenant);
        var category = new Category { TenantId = tenant, Name = "Dining", CreatedAt = T0, UpdatedAt = T0 };
        var bank = new Bank { TenantId = tenant, Name = "BAC Credomatic", CreatedAt = T0, UpdatedAt = T0 };
        db.AddRange(category, bank);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return Build(db, tenant, category.Id, bank.Id, pair is null ? new FixedRate(rate) : new PairRate(pair));
    }

    private static Ctx Build(AppDbContext db, Guid tenant, Guid categoryId, Guid bankId, IExchangeRateResolver resolver)
    {
        var current = new TestCurrentTenant { TenantId = tenant };
        var clock = new FakeTimeProvider(T0);
        var months = new MonthHandler(new EfRepository<Month>(db), new EfRepository<Week>(db), new EfRepository<Transaction>(db), new EfRepository<BudgetSettings>(db), new WeekBoundaryService(), current, clock);
        var transactions = new TransactionHandler(new EfRepository<Transaction>(db), new EfRepository<Refund>(db), new EfRepository<Category>(db), new EfRepository<Bank>(db), new EfRepository<Envelope>(db), new EfRepository<Card>(db), months, resolver, current, clock, NullLogger<TransactionHandler>.Instance);
        var mappings = new MerchantMappingHandler(new EfRepository<MerchantCategoryMapping>(db), new EfRepository<Category>(db), current, clock, NullLogger<MerchantMappingHandler>.Instance);
        var cards = new CardHandler(new EfRepository<Card>(db), new EfRepository<Vuelto.Core.Entities.CardIdentity>(db), new EfRepository<Transaction>(db), new EfRepository<Bank>(db), current, clock);
        var handler = new PendingVoucherHandler(new EfRepository<PendingVoucher>(db), new EfRepository<IngestedVoucher>(db), new EfRepository<EmailConnection>(db), transactions, mappings, cards, new EfUnitOfWork(db), clock, NullLogger<PendingVoucherHandler>.Instance);
        return new Ctx(db, tenant, handler, mappings, categoryId, bankId);
    }

    private Ctx Sibling(Ctx c) => Build(Fixture.CreateContext(c.Tenant), c.Tenant, c.CategoryId, c.BankId, new FixedRate(500m));

    private static async Task<PendingVoucher> DraftAsync(Ctx c, string merchant = "TACO BELL PLAZA REAL C", decimal? amount = 7620m, string? currency = "CRC", DateOnly? date = null, Guid? bankId = null, string status = PendingVoucherStatuses.Pending, DateTimeOffset? receivedAt = null, string[]? missing = null, string? cardNumber = null, string? cardBrand = null)
    {
        var fingerprint = Guid.CreateVersion7().ToString("N");
        var draft = new PendingVoucher
        {
            TenantId = c.Tenant, EmailConnectionId = Guid.CreateVersion7(), ProviderMessageId = fingerprint, Fingerprint = fingerprint, ParsedBank = "Bac",
            BankId = bankId ?? c.BankId, Merchant = merchant, Amount = amount, Currency = currency, Date = date ?? Jun13, Authorization = "662664", CardNumber = cardNumber, CardBrand = cardBrand,
            TransactionType = "COMPRA", MissingFields = missing ?? [], Status = status, ReceivedAt = receivedAt ?? T0, CreatedAt = T0, UpdatedAt = T0,
        };
        c.Db.Add(draft);
        c.Db.Add(new IngestedVoucher { TenantId = c.Tenant, Fingerprint = fingerprint, PendingVoucherId = draft.Id, CreatedAt = T0 });
        await c.Db.SaveChangesAsync();
        c.Db.ChangeTracker.Clear();
        return draft;
    }

    private static ConfirmVoucherRequest Confirm(Ctx c, string cls = "budgeted", bool remember = false, string? notes = null) => new(c.CategoryId, cls, RememberMerchant: remember, Notes: notes);

    private static async Task<PendingVoucher> ReloadAsync(Ctx c, Guid id)
    {
        c.Db.ChangeTracker.Clear();
        return await c.Db.PendingVouchers.SingleAsync(v => v.Id == id);
    }

    [Fact]
    public async Task List_ReturnsPendingDraftsOnly_NewestMailFirst_AndCountsThem()
    {
        var c = await ContextAsync();
        var older = await DraftAsync(c, merchant: "OLD", receivedAt: T0.AddHours(-2));
        var newer = await DraftAsync(c, merchant: "NEW", receivedAt: T0.AddHours(-1));
        await DraftAsync(c, merchant: "DONE", status: PendingVoucherStatuses.Confirmed);
        await DraftAsync(c, merchant: "GONE", status: PendingVoucherStatuses.Discarded);
        var other = await ContextAsync();
        await DraftAsync(other, merchant: "THEIRS");

        var list = await c.Handler.ListPendingAsync(default);
        Assert.Equal([newer.Id, older.Id], list.Select(v => v.Id));
        Assert.Equal(("NEW", 7620m, "CRC", Jun13, c.BankId, "Bac"), (list[0].Merchant, list[0].Amount, list[0].Currency, list[0].Date, list[0].BankId, list[0].ParsedBank));
        Assert.Equal(2, await c.Handler.CountPendingAsync(default));
        Assert.Equal(1, await other.Handler.CountPendingAsync(default));
    }

    [Fact]
    public async Task Clear_RemovesOnlyTheWaitingDrafts_WithTheirTombstones_AndRewindsTheInbox()
    {
        // EMAIL-7: start over on what is still waiting. Confirmed and discarded vouchers keep their
        // tombstones — nothing already booked or thrown away can come back — and the cursor is pulled
        // back so the next sync actually re-reads the cleared mail.
        var c = await ContextAsync();
        var connectionId = Guid.CreateVersion7();
        var owner = new User { Email = $"owner-{connectionId:N}@example.com" }; // EmailConnection is user-keyed and FK'd to Users
        c.Db.Add(owner);
        c.Db.Add(new EmailConnection
        {
            Id = connectionId, UserId = owner.Id, Provider = "microsoft", AccessToken = "t", RefreshToken = "r", SubjectFilters = ["Voucher"],
            ImportFrom = T0.AddDays(-30), LastPolledAt = T0.AddDays(1), PollingIntervalMinutes = 15, Status = "active", CreatedAt = T0, UpdatedAt = T0,
        });
        await c.Db.SaveChangesAsync();
        var waiting = await DraftAsync(c, merchant: "WAITING", receivedAt: T0.AddHours(-3));
        await DraftAsync(c, merchant: "ALSO WAITING", receivedAt: T0.AddHours(-1));
        var booked = await DraftAsync(c, merchant: "DONE", status: PendingVoucherStatuses.Confirmed);
        var thrown = await DraftAsync(c, merchant: "GONE", status: PendingVoucherStatuses.Discarded);
        await c.Db.PendingVouchers.Where(v => v.Status == PendingVoucherStatuses.Pending).ExecuteUpdateAsync(u => u.SetProperty(v => v.EmailConnectionId, connectionId));
        c.Db.ChangeTracker.Clear();

        Assert.Equal("confirmation_required", (await c.Handler.ClearPendingAsync(false, default)).Error!.Error);
        Assert.Equal(2, await c.Handler.CountPendingAsync(default)); // the refusal wrote nothing

        var (result, error) = await c.Handler.ClearPendingAsync(true, default);

        Assert.Null(error);
        Assert.Equal((2, 1), (result!.Cleared, result.InboxesRewound));
        Assert.Equal(0, await c.Handler.CountPendingAsync(default));
        var left = await c.Db.PendingVouchers.Select(v => v.Merchant).ToListAsync();
        Assert.Equal(["DONE", "GONE"], left.Order());
        var tombstones = await c.Db.IngestedVouchers.Select(i => i.PendingVoucherId).ToListAsync();
        Assert.Equal([booked.Id, thrown.Id], tombstones.Order()); // only the acted-on ones still block their email
        Assert.DoesNotContain(waiting.Id, tombstones);
        var connection = await c.Db.EmailConnections.SingleAsync();
        Assert.Equal(T0.AddHours(-3).AddMinutes(-1), connection.LastPolledAt); // just before the oldest cleared draft
    }

    [Fact]
    public async Task Clear_AnEmptyQueue_IsANoOp_AndNeverMovesACursorForward()
    {
        var c = await ContextAsync();
        var connectionId = Guid.CreateVersion7();
        var owner = new User { Email = $"owner-{connectionId:N}@example.com" };
        c.Db.Add(owner);
        c.Db.Add(new EmailConnection
        {
            Id = connectionId, UserId = owner.Id, Provider = "google", AccessToken = "t", RefreshToken = "r", SubjectFilters = ["Voucher"],
            ImportFrom = T0.AddDays(-30), LastPolledAt = T0.AddDays(-29), PollingIntervalMinutes = 15, Status = "active", CreatedAt = T0, UpdatedAt = T0,
        });
        await c.Db.SaveChangesAsync();
        var draft = await DraftAsync(c, receivedAt: T0);
        await c.Db.PendingVouchers.Where(v => v.Id == draft.Id).ExecuteUpdateAsync(u => u.SetProperty(v => v.EmailConnectionId, connectionId));
        c.Db.ChangeTracker.Clear();

        var (first, _) = await c.Handler.ClearPendingAsync(true, default);
        Assert.Equal((1, 0), (first!.Cleared, first.InboxesRewound)); // the cursor already sat further back — leave it there
        Assert.Equal(T0.AddDays(-29), (await c.Db.EmailConnections.SingleAsync()).LastPolledAt);

        var (again, error) = await c.Handler.ClearPendingAsync(true, default);
        Assert.Null(error);
        Assert.Equal((0, 0), (again!.Cleared, again.InboxesRewound));
    }

    [Fact]
    public async Task Confirm_BooksAnEmailTransaction_ThroughTheOrdinaryCreate_AndFlipsTheDraft()
    {
        var c = await ContextAsync();
        var draft = await DraftAsync(c);

        var (confirmed, error) = await c.Handler.ConfirmAsync(draft.Id, Confirm(c, "extraordinary", notes: "  Lunch with the team  "), default);

        Assert.Null(error);
        var tx = await c.Db.Transactions.SingleAsync();
        Assert.Equal("Lunch with the team", tx.Notes); // the reason, recorded at confirm time, trimmed by the ledger
        Assert.Equal((confirmed!.TransactionId, TransactionSources.Email, "extraordinary", "TACO BELL PLAZA REAL C", 7620m, "CRC", Jun13, c.BankId, c.CategoryId, "credit_card", 500m, 7620m, 15.24m),
            (tx.Id, tx.Source, tx.TransactionType, tx.Payee, tx.OriginalAmount, tx.Currency, tx.TransactionDate, tx.BankId, tx.CategoryId, tx.PaymentMethod, tx.ExchangeRateUsed, tx.AmountCrc, tx.AmountUsd));
        var month = await c.Db.Months.SingleAsync(); // auto-created from the voucher date (ADR-V005)
        Assert.Equal((month.Id, 2026, 6), (tx.MonthId, month.Year, month.MonthNumber));
        Assert.Equal((month.Id, 7620m, 15.24m, false), (confirmed.MonthId, confirmed.AmountCrc, confirmed.AmountUsd, confirmed.Remembered));

        var stored = await ReloadAsync(c, draft.Id);
        Assert.Equal((PendingVoucherStatuses.Confirmed, tx.Id), (stored.Status, stored.ConfirmedTransactionId));
        Assert.Equal(1, await c.Db.IngestedVouchers.CountAsync()); // the tombstone outlives the draft
        Assert.Equal(0, await c.Handler.CountPendingAsync(default));
    }

    [Fact]
    public async Task Confirm_FreezesTheRateForTheVouchersCurrency_DollarsAtSell_ColonesAtBuy()
    {
        // ADR-V019: BCCR 2026-09-07 — compra 448.27 (Buy), venta 453.69 (Sell). A $ voucher costs colones at venta;
        // a ₡ voucher is worth dollars at compra (you would sell dollars to fund it). The frozen rate is the side used.
        var c = await ContextAsync(pair: new FxRates(Buy: 448.27m, Sell: 453.69m));
        var colones = await DraftAsync(c);                                                  // ₡7,620 (TACO BELL)
        var dollars = await DraftAsync(c, merchant: "AMAZON", amount: 20m, currency: "USD"); // $20

        var (crc, e1) = await c.Handler.ConfirmAsync(colones.Id, Confirm(c), default);
        var (usd, e2) = await c.Handler.ConfirmAsync(dollars.Id, Confirm(c), default);

        Assert.Null(e1); Assert.Null(e2);
        var crcTx = await c.Db.Transactions.SingleAsync(t => t.Id == crc!.TransactionId);
        var usdTx = await c.Db.Transactions.SingleAsync(t => t.Id == usd!.TransactionId);
        Assert.Equal((448.27m, 7620m, 17.00m), (crcTx.ExchangeRateUsed, crcTx.AmountCrc, crcTx.AmountUsd));   // 7,620 / 448.27
        Assert.Equal((453.69m, 9073.80m, 20m), (usdTx.ExchangeRateUsed, usdTx.AmountCrc, usdTx.AmountUsd));   // 20 × 453.69
    }

    [Fact]
    public async Task Confirm_LinksTheCardTheVoucherPrinted_CreatingItOnFirstSight_AndReusingItAfter()
    {
        // CARDS-1: two vouchers on the same card → one card row (VISA-1234, auto-named, on the voucher's bank), both transactions on it;
        // a voucher without a card number → no card. Nothing for the user to do at confirm time.
        var c = await ContextAsync();
        var first = await DraftAsync(c, cardNumber: "************1234", cardBrand: "VISA");
        var second = await DraftAsync(c, merchant: "AUTOMERCADO", amount: 15_000m, cardNumber: "************1234", cardBrand: "VISA");
        var bare = await DraftAsync(c, merchant: "ICE", amount: 29_730m);

        var (t1, e1) = await c.Handler.ConfirmAsync(first.Id, Confirm(c), default);
        var (t2, e2) = await c.Handler.ConfirmAsync(second.Id, Confirm(c), default);
        var (t3, e3) = await c.Handler.ConfirmAsync(bare.Id, Confirm(c), default);

        Assert.Null(e1); Assert.Null(e2); Assert.Null(e3);
        var card = await c.Db.Cards.SingleAsync();
        Assert.Equal(("VISA-1234", "VISA", "1234", true, c.BankId), (card.Name, card.Brand, card.Last4, card.AutoNamed, card.BankId));
        var byId = await c.Db.Transactions.ToDictionaryAsync(t => t.Id, t => t.CardId);
        Assert.Equal((card.Id, card.Id, null), (byId[t1!.TransactionId], byId[t2!.TransactionId], byId[t3!.TransactionId]));
    }

    [Fact]
    public async Task Confirm_AsUnplannedWithAPercentage_SpawnsThePendingRefund_WithTheManualRules()
    {
        var c = await ContextAsync();
        var draft = await DraftAsync(c);

        // The manual form's rules, through the same ledger create: an invalid percentage is refused and nothing is written.
        Assert.Equal("invalid_request", (await c.Handler.ConfirmAsync(draft.Id, new(c.CategoryId, "unplanned_essential", RefundExpected: true, RefundPercentage: 150m), default)).Error!.Error);
        Assert.Equal("invalid_request", (await c.Handler.ConfirmAsync(draft.Id, new(c.CategoryId, "unplanned_essential", RefundExpected: true), default)).Error!.Error);
        Assert.Equal(0, await c.Db.Transactions.CountAsync());
        Assert.Equal(PendingVoucherStatuses.Pending, (await ReloadAsync(c, draft.Id)).Status);

        var (confirmed, error) = await c.Handler.ConfirmAsync(draft.Id, new(c.CategoryId, "unplanned_essential", RefundExpected: true, RefundPercentage: 30m), default);

        Assert.Null(error);
        var tx = await c.Db.Transactions.SingleAsync();
        var refund = await c.Db.Refunds.SingleAsync();
        // 30 % of ₡7,620 / $15.24 at the frozen rate, pending, in the voucher's month, bound to the booked transaction.
        Assert.Equal((tx.Id, tx.MonthId, 30m, 2_286m, 4.57m, RefundStatuses.Pending, "TACO BELL PLAZA REAL C"),
            (refund.TransactionId, refund.MonthId, refund.Percentage, refund.AmountCrc, refund.AmountUsd, refund.Status, refund.Payee));
        Assert.Equal((PendingVoucherStatuses.Confirmed, confirmed!.TransactionId), ((await ReloadAsync(c, draft.Id)).Status, tx.Id));
    }

    [Fact]
    public async Task Confirm_RefundFlagOnAnotherClass_IsIgnored_NoRefund()
    {
        var c = await ContextAsync();
        var draft = await DraftAsync(c);

        var (confirmed, error) = await c.Handler.ConfirmAsync(draft.Id, new(c.CategoryId, "budgeted", RefundExpected: true, RefundPercentage: 30m), default);

        Assert.Null(error);
        Assert.NotNull(confirmed);
        Assert.Equal(0, await c.Db.Refunds.CountAsync());
    }

    [Fact]
    public async Task Confirm_AppliesOverrides_ForTheFieldsTheParserLeftBlank()
    {
        var c = await ContextAsync();
        var draft = await DraftAsync(c, amount: null, currency: null, date: null, missing: ["Amount", "Currency"]);
        var (_, blank) = await c.Handler.ConfirmAsync(draft.Id, Confirm(c), default);
        Assert.Equal("invalid_request", blank!.Error); // 0 amount → the ledger rejects; nothing written
        Assert.Empty(await c.Db.Transactions.ToListAsync());
        Assert.Equal(PendingVoucherStatuses.Pending, (await ReloadAsync(c, draft.Id)).Status);

        var (confirmed, error) = await c.Handler.ConfirmAsync(draft.Id, new(c.CategoryId, "budgeted", Payee: "Taco Bell", OriginalAmount: 12.5m, Currency: "usd", TransactionDate: new DateOnly(2026, 7, 10), PaymentMethod: "bank_account"), default);
        Assert.Null(error);
        var tx = await c.Db.Transactions.SingleAsync(t => t.Id == confirmed!.TransactionId);
        Assert.Equal(("Taco Bell", 12.5m, "USD", new DateOnly(2026, 7, 10), "bank_account", 6250m, 12.5m), (tx.Payee, tx.OriginalAmount, tx.Currency, tx.TransactionDate, tx.PaymentMethod, tx.AmountCrc, tx.AmountUsd));
    }

    [Fact]
    public async Task Confirm_RejectsAMissingCategoryOrClass_AndWritesNothing()
    {
        var c = await ContextAsync();
        var draft = await DraftAsync(c);
        Assert.Equal("invalid_request", (await c.Handler.ConfirmAsync(draft.Id, new(null, "budgeted"), default)).Error!.Error);
        Assert.Equal("invalid_request", (await c.Handler.ConfirmAsync(draft.Id, new(Guid.Empty, "budgeted"), default)).Error!.Error);
        Assert.Equal("invalid_request", (await c.Handler.ConfirmAsync(draft.Id, new(c.CategoryId, "inflow"), default)).Error!.Error);
        Assert.Equal("invalid_request", (await c.Handler.ConfirmAsync(draft.Id, new(c.CategoryId, null), default)).Error!.Error);
        Assert.Equal("invalid_request", (await c.Handler.ConfirmAsync(draft.Id, new(Guid.CreateVersion7(), "budgeted"), default)).Error!.Error); // unknown category → the ledger's own check
        Assert.Empty(await c.Db.Transactions.ToListAsync());
        Assert.Empty(await c.Db.Months.ToListAsync());
        Assert.Equal(PendingVoucherStatuses.Pending, (await ReloadAsync(c, draft.Id)).Status);
    }

    [Fact]
    public async Task Confirm_WithoutARate_WritesNothing_AndTheDraftStaysPending()
    {
        var c = await ContextAsync(rate: null);
        var draft = await DraftAsync(c);
        var (_, error) = await c.Handler.ConfirmAsync(draft.Id, Confirm(c), default);
        Assert.Equal("exchange_rate_unavailable", error!.Error);
        Assert.Empty(await c.Db.Transactions.ToListAsync());
        Assert.Empty(await c.Db.Months.ToListAsync());
        Assert.Equal(PendingVoucherStatuses.Pending, (await ReloadAsync(c, draft.Id)).Status);
    }

    [Fact]
    public async Task ConcurrentConfirms_BookExactlyOneTransaction_TheLoserGetsNotPending()
    {
        var c = await ContextAsync();
        var draft = await DraftAsync(c);
        var one = Sibling(c);
        var two = Sibling(c);

        var results = await Task.WhenAll(
            Task.Run(() => one.Handler.ConfirmAsync(draft.Id, Confirm(c), default)),
            Task.Run(() => two.Handler.ConfirmAsync(draft.Id, Confirm(c), default)));

        Assert.Equal(1, results.Count(r => r.Error is null));
        Assert.Equal("not_pending", Assert.Single(results, r => r.Error is not null).Error!.Error);
        await using var verify = Fixture.CreateContext(c.Tenant);
        var tx = await verify.Transactions.SingleAsync(); // the loser's create rolled back with its scope
        Assert.Equal(1, await verify.Months.CountAsync());
        var stored = await verify.PendingVouchers.SingleAsync(v => v.Id == draft.Id);
        Assert.Equal((PendingVoucherStatuses.Confirmed, tx.Id), (stored.Status, stored.ConfirmedTransactionId));
    }

    [Fact]
    public async Task Confirm_ThenConfirmOrDiscardAgain_IsNotPending_AndDiscardIsAGuardedFlip()
    {
        var c = await ContextAsync();
        var draft = await DraftAsync(c);
        Assert.Null((await c.Handler.ConfirmAsync(draft.Id, Confirm(c), default)).Error);
        Assert.Equal("not_pending", (await c.Handler.ConfirmAsync(draft.Id, Confirm(c), default)).Error!.Error);
        Assert.Equal("not_pending", (await c.Handler.DiscardAsync(draft.Id, default))!.Error);
        Assert.Equal(PendingVoucherStatuses.Confirmed, (await ReloadAsync(c, draft.Id)).Status); // never reverted
        Assert.Equal(1, await c.Db.Transactions.CountAsync());

        var other = await DraftAsync(c, merchant: "WALMART");
        Assert.Null(await c.Handler.DiscardAsync(other.Id, default));
        Assert.Equal(PendingVoucherStatuses.Discarded, (await ReloadAsync(c, other.Id)).Status);
        Assert.Equal("not_pending", (await c.Handler.ConfirmAsync(other.Id, Confirm(c), default)).Error!.Error);
        Assert.Equal(2, await c.Db.IngestedVouchers.CountAsync()); // tombstones stay through both flips
        Assert.Equal(1, await c.Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task ForeignOrUnknownIds_AreAUniform404()
    {
        var c = await ContextAsync();
        var draft = await DraftAsync(c);
        var other = await ContextAsync();
        Assert.Equal("not_found", (await other.Handler.ConfirmAsync(draft.Id, Confirm(other), default)).Error!.Error);
        Assert.Equal("not_found", (await other.Handler.DiscardAsync(draft.Id, default))!.Error);
        Assert.Equal("not_found", (await c.Handler.DiscardAsync(Guid.CreateVersion7(), default))!.Error);
        Assert.Equal(PendingVoucherStatuses.Pending, (await ReloadAsync(c, draft.Id)).Status);
    }

    [Fact]
    public async Task Confirm_RememberMerchant_CreatesARule_AndNeverOverwritesOne()
    {
        var c = await ContextAsync();
        var first = await DraftAsync(c);
        var (confirmed, _) = await c.Handler.ConfirmAsync(first.Id, Confirm(c, "extraordinary", remember: true), default);
        Assert.True(confirmed!.Remembered);
        var rule = Assert.Single(await c.Mappings.ListAsync(default));
        Assert.Equal(("TACO BELL PLAZA REAL C", c.CategoryId, "extraordinary"), (rule.MerchantPattern, rule.CategoryId, rule.SuggestedClass));

        var second = await DraftAsync(c, merchant: "taco bell plaza real c");
        var (again, _) = await c.Handler.ConfirmAsync(second.Id, Confirm(c, "budgeted", remember: true), default);
        Assert.False(again!.Remembered);
        Assert.Equal("extraordinary", Assert.Single(await c.Mappings.ListAsync(default)).SuggestedClass);

        var blank = await DraftAsync(c, merchant: null!, missing: ["Merchant"]);
        var (named, _) = await c.Handler.ConfirmAsync(blank.Id, new(c.CategoryId, "budgeted", Payee: "Manual name", RememberMerchant: true), default);
        Assert.False(named!.Remembered); // nothing parsed to remember
        Assert.Single(await c.Mappings.ListAsync(default));
    }
}
