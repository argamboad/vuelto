using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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

    private sealed record Ctx(AppDbContext Db, Guid Tenant, PendingVoucherHandler Handler, MerchantMappingHandler Mappings, Guid CategoryId, Guid BankId);

    private async Task<Ctx> ContextAsync(decimal? rate = 500m)
    {
        var tenant = Guid.CreateVersion7();
        var db = Fixture.CreateContext(tenant);
        var category = new Category { TenantId = tenant, Name = "Dining", CreatedAt = T0, UpdatedAt = T0 };
        var bank = new Bank { TenantId = tenant, Name = "BAC Credomatic", CreatedAt = T0, UpdatedAt = T0 };
        db.AddRange(category, bank);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return Build(db, tenant, category.Id, bank.Id, rate);
    }

    private static Ctx Build(AppDbContext db, Guid tenant, Guid categoryId, Guid bankId, decimal? rate)
    {
        var current = new TestCurrentTenant { TenantId = tenant };
        var clock = new FakeTimeProvider(T0);
        var months = new MonthHandler(new EfRepository<Month>(db), new EfRepository<Week>(db), new EfRepository<Transaction>(db), new EfRepository<BudgetSettings>(db), new WeekBoundaryService(), current, clock);
        var transactions = new TransactionHandler(new EfRepository<Transaction>(db), new EfRepository<Refund>(db), new EfRepository<Category>(db), new EfRepository<Bank>(db), new EfRepository<Envelope>(db), months, new FixedRate(rate), current, clock, NullLogger<TransactionHandler>.Instance);
        var mappings = new MerchantMappingHandler(new EfRepository<MerchantCategoryMapping>(db), new EfRepository<Category>(db), current, clock, NullLogger<MerchantMappingHandler>.Instance);
        var handler = new PendingVoucherHandler(new EfRepository<PendingVoucher>(db), transactions, mappings, new EfUnitOfWork(db), clock, NullLogger<PendingVoucherHandler>.Instance);
        return new Ctx(db, tenant, handler, mappings, categoryId, bankId);
    }

    private Ctx Sibling(Ctx c) => Build(Fixture.CreateContext(c.Tenant), c.Tenant, c.CategoryId, c.BankId, 500m);

    private static async Task<PendingVoucher> DraftAsync(Ctx c, string merchant = "TACO BELL PLAZA REAL C", decimal? amount = 7620m, string? currency = "CRC", DateOnly? date = null, Guid? bankId = null, string status = PendingVoucherStatuses.Pending, DateTimeOffset? receivedAt = null, string[]? missing = null)
    {
        var fingerprint = Guid.CreateVersion7().ToString("N");
        var draft = new PendingVoucher
        {
            TenantId = c.Tenant, EmailConnectionId = Guid.CreateVersion7(), ProviderMessageId = fingerprint, Fingerprint = fingerprint, ParsedBank = "Bac",
            BankId = bankId ?? c.BankId, Merchant = merchant, Amount = amount, Currency = currency, Date = date ?? Jun13, Authorization = "662664",
            TransactionType = "COMPRA", MissingFields = missing ?? [], Status = status, ReceivedAt = receivedAt ?? T0, CreatedAt = T0, UpdatedAt = T0,
        };
        c.Db.Add(draft);
        c.Db.Add(new IngestedVoucher { TenantId = c.Tenant, Fingerprint = fingerprint, PendingVoucherId = draft.Id, CreatedAt = T0 });
        await c.Db.SaveChangesAsync();
        c.Db.ChangeTracker.Clear();
        return draft;
    }

    private static ConfirmVoucherRequest Confirm(Ctx c, string cls = "budgeted", bool remember = false) => new(c.CategoryId, cls, RememberMerchant: remember);

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
    public async Task Confirm_BooksAnEmailTransaction_ThroughTheOrdinaryCreate_AndFlipsTheDraft()
    {
        var c = await ContextAsync();
        var draft = await DraftAsync(c);

        var (confirmed, error) = await c.Handler.ConfirmAsync(draft.Id, Confirm(c, "extraordinary"), default);

        Assert.Null(error);
        var tx = await c.Db.Transactions.SingleAsync();
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
