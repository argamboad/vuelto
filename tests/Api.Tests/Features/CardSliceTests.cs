using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Features.Cards;
using Vuelto.Api.Features.Ledger;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Features;

/// <summary>
/// CARDS-1 on real Postgres (ADR-V021): a manual create normalises brand + last four and refuses the two kinds of
/// duplicate (alias, identity) with the catalog's 409 shape; the voucher path auto-names a card on first sight and
/// reuses it after; a rename is the household's word (auto flag off); inactive stays visible to history; tenant
/// isolation; the contributor.
/// </summary>
[Collection(PostgresCollection.Name)]
public class CardSliceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed record Ctx(AppDbContext Db, CardHandler Handler, Guid Tenant, Guid BankId);

    private async Task<Ctx> ContextAsync(Guid? tenantId = null)
    {
        var tenant = tenantId ?? Guid.CreateVersion7();
        var db = Fixture.CreateContext(tenant);
        var bank = new Bank { TenantId = tenant, Name = "BAC Credomatic", CreatedAt = T0, UpdatedAt = T0 };
        db.Add(bank);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Ctx(db, new CardHandler(new EfRepository<Card>(db), new EfRepository<CardIdentity>(db), new EfRepository<CardPattern>(db), new TransactionCards(new EfRepository<Transaction>(db)), new EfRepository<Bank>(db), new TestCurrentTenant { TenantId = tenant }, new FakeTimeProvider(T0)), tenant, bank.Id);
    }

    [Fact]
    public async Task Create_NormalisesBrandAndLastFour_AndListsIt()
    {
        var c = await ContextAsync();

        var (card, error) = await c.Handler.CreateAsync(new CreateCardRequest("  Allan's Visa ", "visa", "************1234", c.BankId), default);

        Assert.Null(error);
        Assert.Equal(("Allan's Visa", "VISA", "1234", c.BankId, true, false), (card!.Name, card.Brand, card.Last4, card.BankId, card.IsActive, card.AutoNamed));
        Assert.Equal("Allan's Visa", Assert.Single((await c.Handler.ListAsync(false, default))!).Name);
    }

    [Theory]
    [InlineData(null, "1234", "name is required")]
    [InlineData("Card", "12", "last4")]
    [InlineData("Card", "abcd", "last4")]
    public async Task Create_RejectsAMissingAliasOrLastFour(string? name, string last4, string mentions)
    {
        var c = await ContextAsync();
        var (card, error) = await c.Handler.CreateAsync(new CreateCardRequest(name, "VISA", last4, null), default);
        Assert.Null(card);
        Assert.Equal("invalid_request", error!.Error);
        Assert.Contains(mentions, error.Message);
    }

    [Fact]
    public async Task Create_ClashesOnAlias_OrOnIdentity_With409()
    {
        var c = await ContextAsync();
        var first = (await c.Handler.CreateAsync(new CreateCardRequest("Main", "VISA", "1234", null), default)).Card!;

        var (_, alias) = await c.Handler.CreateAsync(new CreateCardRequest("main", "MASTERCARD", "9999", null), default);
        var (_, identity) = await c.Handler.CreateAsync(new CreateCardRequest("Another alias", "visa", "****1234", null), default);

        var a = Assert.IsType<CardConflictResponse>(alias);
        Assert.Equal(("card_exists", first.Id), (a.Error, a.ExistingId));
        var i = Assert.IsType<CardConflictResponse>(identity);
        Assert.Equal(("card_exists", first.Id, "Main"), (i.Error, i.ExistingId, i.ExistingName));
    }

    [Fact]
    public async Task InactiveClash_OffersReactivation_AndUpdateReactivates()
    {
        var c = await ContextAsync();
        var card = (await c.Handler.CreateAsync(new CreateCardRequest("Old card", "VISA", "1234", null), default)).Card!;
        await c.Handler.UpdateAsync(card.Id, new UpdateCardRequest("Old card", null, IsActive: false), default);

        var (_, error) = await c.Handler.CreateAsync(new CreateCardRequest("old CARD", "AMEX", "0001", null), default);
        var conflict = Assert.IsType<CardConflictResponse>(error);
        Assert.Equal(("card_exists_inactive", card.Id, "Old card"), (conflict.Error, conflict.ExistingId, conflict.ExistingName));

        var (revived, _) = await c.Handler.UpdateAsync(card.Id, new UpdateCardRequest("Old card", null, IsActive: true), default);
        Assert.True(revived!.IsActive);
        Assert.Single((await c.Handler.ListAsync(false, default))!);
    }

    [Fact]
    public async Task ResolveOrCreate_AutoNamesOnFirstSight_ReusesAfter_AndHonoursTheBrandlessReceipt()
    {
        var c = await ContextAsync();

        var first = await c.Handler.ResolveOrCreateAsync("VISA", "************1234", c.BankId, default);
        var again = await c.Handler.ResolveOrCreateAsync("visa", "4111 1111 1111 1234", null, default); // same card, however the bank prints it
        var bn = await c.Handler.ResolveOrCreateAsync(null, "XXXXXXXXXXX0000X", c.BankId, default);      // BN payment receipt: digits then a mask (#210)
        var none = await c.Handler.ResolveOrCreateAsync("VISA", null, c.BankId, default);

        Assert.NotNull(first);
        Assert.Equal(first, again);
        Assert.Null(none);
        Assert.Null(bn); // its digits aren't the last four — no guessed CARD-0000 (#210, ADR-V027)
        var card = Assert.Single((await c.Handler.ListAsync(true, default))!);
        Assert.Equal(("VISA-1234", true), (card.Name, card.AutoNamed));
    }

    // ---- #210 (ADR-V027): a pattern is mapped once, by the household, and remembered ----

    [Fact]
    public async Task ResolveChosen_RemembersThePattern_TheNextVoucherFindsIt_AndADifferentAnswerReplacesIt()
    {
        var c = await ContextAsync();
        var black = (await c.Handler.ResolveOrCreateAsync("VISA", "************7558", c.BankId, default))!.CardId;
        var other = (await c.Handler.ResolveOrCreateAsync("VISA", "************1234", c.BankId, default))!.CardId;
        Assert.Null(await c.Handler.ResolveOrCreateAsync(null, "XXXXXXXXXXX8755X", c.BankId, default)); // nobody has said yet

        Assert.Equal(black, (await c.Handler.ResolveChosenAsync(black, "XXXXXXXXXXX8755X", default))!.CardId);

        Assert.Equal(black, (await c.Handler.ResolveOrCreateAsync(null, "xxxx xxxx xxx8 755x", c.BankId, default))!.CardId); // however it is spaced
        Assert.Equal(black, (await c.Handler.KnownPatternsAsync(["XXXXXXXXXXX8755X", "XXXXXXXXXXX0000X"], default))["XXXXXXXXXXX8755X"]);

        await c.Handler.ResolveChosenAsync(other, "XXXXXXXXXXX8755X", default); // the household corrects itself
        Assert.Equal(other, (await c.Handler.ResolveOrCreateAsync(null, "XXXXXXXXXXX8755X", c.BankId, default))!.CardId);
        Assert.Equal(1, await c.Db.CardPatterns.CountAsync());
        Assert.Equal(2, await c.Db.Cards.CountAsync()); // and never a third, guessed card
    }

    [Fact]
    public async Task ResolveChosen_ForAPlainNumber_RemembersNothing_AndRefusesAnInactiveOrForeignCard()
    {
        var c = await ContextAsync();
        var card = (await c.Handler.ResolveOrCreateAsync("VISA", "************1234", c.BankId, default))!.CardId;

        Assert.Equal(card, (await c.Handler.ResolveChosenAsync(card, "************9999", default))!.CardId);
        Assert.Equal(0, await c.Db.CardPatterns.CountAsync());

        Assert.Null(await c.Handler.ResolveChosenAsync(Guid.CreateVersion7(), "XXXXXXXXXXX8755X", default));
        await c.Handler.UpdateAsync(card, new UpdateCardRequest("VISA-1234", c.BankId, IsActive: false), default);
        Assert.Null(await c.Handler.ResolveChosenAsync(card, "XXXXXXXXXXX8755X", default));

        var b = await ContextAsync();
        Assert.Null(await b.Handler.ResolveChosenAsync(card, "XXXXXXXXXXX8755X", default)); // another household's card does not exist here
        Assert.Equal(0, await c.Db.CardPatterns.CountAsync());
    }

    [Fact]
    public async Task Merge_MovesTheRememberedPatterns_ToTheSurvivor()
    {
        var c = await ContextAsync();
        var old = (await c.Handler.ResolveOrCreateAsync("VISA", "************1111", c.BankId, default))!.CardId;
        var renewed = (await c.Handler.ResolveOrCreateAsync("VISA", "************2222", c.BankId, default))!.CardId;
        await c.Handler.ResolveChosenAsync(old, "XXXXXXXXXXX8755X", default);
        c.Db.ChangeTracker.Clear(); // a fresh request, as in production — the merge moves rows with set-based updates

        await c.Handler.MergeAsync(old, renewed, default);

        Assert.Equal(renewed, (await c.Handler.ResolveOrCreateAsync(null, "XXXXXXXXXXX8755X", null, default))!.CardId);
    }

    [Fact]
    public async Task Rename_ClearsTheAutoFlag_AndTheVoucherPathStillFindsTheCard()
    {
        var c = await ContextAsync();
        var id = (await c.Handler.ResolveOrCreateAsync("VISA", "************1234", null, default))!.CardId;

        var (renamed, error) = await c.Handler.UpdateAsync(id, new UpdateCardRequest("Tarjeta de Allan", c.BankId, IsActive: true), default);

        Assert.Null(error);
        Assert.Equal(("Tarjeta de Allan", false, c.BankId), (renamed!.Name, renamed.AutoNamed, renamed.BankId));
        Assert.Equal(id, (await c.Handler.ResolveOrCreateAsync("VISA", "************1234", null, default))!.CardId); // identity, not alias, is the key
    }

    [Fact]
    public async Task ResolveOrCreate_ABrandlessNumber_IsTheCardAlreadyKnownByIt_AndABrandUpgradesAPlaceholder()
    {
        // A BN payment prints no brand; a draft staged before brand capture carries none. One plastic, one card.
        var c = await ContextAsync();
        var visa = (await c.Handler.ResolveOrCreateAsync("VISA", "************1234", c.BankId, default))!.CardId;
        Assert.Equal(visa, (await c.Handler.ResolveOrCreateAsync(null, "************1234", null, default))!.CardId); // brandless → the Visa

        var placeholder = (await c.Handler.ResolveOrCreateAsync(null, "************1966", c.BankId, default))!.CardId;
        Assert.Equal("CARD-1966", (await c.Handler.ListAsync(true, default))!.Single(x => x.Id == placeholder).Name);

        Assert.Equal(placeholder, (await c.Handler.ResolveOrCreateAsync("VISA", "************1966", null, default))!.CardId); // the brand arrives → same card, upgraded
        var upgraded = (await c.Handler.ListAsync(true, default))!.Single(x => x.Id == placeholder);
        Assert.Equal(("VISA-1966", "VISA", "1966", true), (upgraded.Name, upgraded.Brand, upgraded.Last4, upgraded.AutoNamed));
        Assert.Equal([("VISA", "1966")], upgraded.Identities.Select(i => (i.Brand, i.Last4)));
        Assert.Equal(2, (await c.Handler.ListAsync(true, default))!.Count);
    }

    [Fact]
    public async Task Merge_ARenewedCardIntoTheOriginal_MovesIdentitiesAndTransactions_AndTheNextVoucherFindsTheSurvivor()
    {
        // The bank renewed the plastic: a new last four arrived as VISA-5678. "Same card" → one card, two identities, whole history.
        var c = await ContextAsync();
        var original = (await c.Handler.ResolveOrCreateAsync("VISA", "************1234", c.BankId, default))!.CardId;
        await c.Handler.UpdateAsync(original, new UpdateCardRequest("Allan's Visa", c.BankId, IsActive: true), default);
        var renewed = (await c.Handler.ResolveOrCreateAsync("VISA", "************5678", c.BankId, default))!.CardId;
        var category = new Category { TenantId = c.Tenant, Name = "Groceries", CreatedAt = T0, UpdatedAt = T0 };
        var month = new Month { TenantId = c.Tenant, Year = 2026, MonthNumber = 9, WeekCount = 5, Week1StartDate = new DateOnly(2026, 8, 25), CreatedAt = T0, UpdatedAt = T0 };
        c.Db.AddRange(category, month,
            new Transaction { TenantId = c.Tenant, MonthId = month.Id, BankId = c.BankId, CategoryId = category.Id, CardId = renewed, Payee = "New plastic", OriginalAmount = 1m, TransactionDate = new DateOnly(2026, 9, 1), AmountCrc = 1m, AmountUsd = 0.01m, ExchangeRateUsed = 500m, CreatedAt = T0, UpdatedAt = T0 },
            new Transaction { TenantId = c.Tenant, MonthId = month.Id, BankId = c.BankId, CategoryId = category.Id, CardId = original, Payee = "Old plastic", OriginalAmount = 1m, TransactionDate = new DateOnly(2026, 9, 1), AmountCrc = 1m, AmountUsd = 0.01m, ExchangeRateUsed = 500m, CreatedAt = T0, UpdatedAt = T0 });
        await c.Db.SaveChangesAsync();
        c.Db.ChangeTracker.Clear();

        var (merged, error) = await c.Handler.MergeAsync(renewed, original, default);

        Assert.Null(error);
        Assert.Equal(("Allan's Visa", "5678", false), (merged!.Name, merged.Last4, merged.AutoNamed)); // the alias survives, the newest number shows
        Assert.Equal(["1234", "5678"], merged.Identities.Select(i => i.Last4));
        Assert.Single((await c.Handler.ListAsync(true, default))!);
        Assert.All(await c.Db.Transactions.ToListAsync(), t => Assert.Equal(original, t.CardId));
        Assert.Equal(original, (await c.Handler.ResolveOrCreateAsync("VISA", "************5678", null, default))!.CardId); // the renewed number now finds the survivor
        Assert.Equal(original, (await c.Handler.ResolveOrCreateAsync("VISA", "************1234", null, default))!.CardId);

        Assert.Equal("invalid_request", (await c.Handler.MergeAsync(original, original, default)).Error!.Error);
        Assert.Equal("not_found", (await c.Handler.MergeAsync(original, Guid.CreateVersion7(), default)).Error!.Error);
    }

    [Fact]
    public async Task Kind_DefaultsToCredit_DecidesThePaymentMethod_AndBackfillsOnlyWhenAsked()
    {
        // CARDS-3: the card knows how money leaves. Flipping it to debit changes what gets booked from now on;
        // the past is corrected only on request, and then it really is corrected.
        var c = await ContextAsync();
        var (card, _) = await c.Handler.CreateAsync(new CreateCardRequest("Allan's Visa", "VISA", "1234", c.BankId), default);
        Assert.Equal("credit", card!.Kind); // no kind given → credit, like every card from before this slice

        var category = new Category { TenantId = c.Tenant, Name = "Groceries", CreatedAt = T0, UpdatedAt = T0 };
        var month = new Month { TenantId = c.Tenant, Year = 2026, MonthNumber = 9, WeekCount = 5, Week1StartDate = new DateOnly(2026, 8, 25), CreatedAt = T0, UpdatedAt = T0 };
        c.Db.AddRange(category, month,
            new Transaction { TenantId = c.Tenant, MonthId = month.Id, BankId = c.BankId, CategoryId = category.Id, CardId = card.Id, Payee = "Super", PaymentMethod = "credit_card", OriginalAmount = 1m, TransactionDate = new DateOnly(2026, 9, 1), AmountCrc = 1m, AmountUsd = 0.01m, ExchangeRateUsed = 500m, CreatedAt = T0, UpdatedAt = T0 });
        await c.Db.SaveChangesAsync();
        c.Db.ChangeTracker.Clear();

        // Flip to debit WITHOUT the backfill: history is left exactly as it was.
        var (quiet, _) = await c.Handler.UpdateAsync(card.Id, new UpdateCardRequest("Allan's Visa", c.BankId, IsActive: true, Kind: "debit"), default);
        Assert.Equal(("debit", null), (quiet!.Kind, quiet.Backfilled));
        Assert.Equal("credit_card", (await c.Db.Transactions.AsNoTracking().SingleAsync()).PaymentMethod);

        // Ask for it, and the card's past rows are brought in line.
        var (corrected, _) = await c.Handler.UpdateAsync(card.Id, new UpdateCardRequest("Allan's Visa", c.BankId, IsActive: true, Kind: "debit", BackfillPaymentMethod: true), default);
        Assert.Equal(1, corrected!.Backfilled);
        Assert.Equal("bank_account", (await c.Db.Transactions.AsNoTracking().SingleAsync()).PaymentMethod);

        // Nothing left to correct the second time, and an unknown kind is refused.
        Assert.Equal(0, (await c.Handler.UpdateAsync(card.Id, new UpdateCardRequest("Allan's Visa", c.BankId, IsActive: true, Kind: "debit", BackfillPaymentMethod: true), default)).Card!.Backfilled);
        Assert.Equal("invalid_request", (await c.Handler.UpdateAsync(card.Id, new UpdateCardRequest("Allan's Visa", c.BankId, IsActive: true, Kind: "prepaid"), default)).Error!.Error);
    }

    [Fact]
    public async Task ResolveOrCreate_TakesTheKindTheVoucherNamed_ButNeverRewritesAnExistingCard()
    {
        var c = await ContextAsync();

        var debit = await c.Handler.ResolveOrCreateAsync("VISA", "************4444", c.BankId, "debit", default);
        Assert.Equal(("debit", "bank_account"), (debit!.Kind, debit.PaymentMethod));

        // The same card seen again, this time with no word on the kind (or the wrong one): the household's card wins.
        Assert.Equal("debit", (await c.Handler.ResolveOrCreateAsync("VISA", "************4444", null, null, default))!.Kind);
        Assert.Equal("debit", (await c.Handler.ResolveOrCreateAsync("VISA", "************4444", null, "credit", default))!.Kind);

        var silent = await c.Handler.ResolveOrCreateAsync("VISA", "************5555", c.BankId, null, default);
        Assert.Equal(("credit", "credit_card"), (silent!.Kind, silent.PaymentMethod)); // no word → credit
    }

    [Fact]
    public async Task Cards_AreInvisibleAndUnwritable_AcrossTenants()
    {
        var a = await ContextAsync();
        var b = await ContextAsync();
        var aCard = (await a.Handler.CreateAsync(new CreateCardRequest("A only", "VISA", "1234", null), default)).Card!;

        Assert.Empty((await b.Handler.ListAsync(true, default))!);
        Assert.Equal("not_found", (await b.Handler.UpdateAsync(aCard.Id, new UpdateCardRequest("Hijacked", null, false), default)).Error!.Error);
        Assert.Null((await b.Handler.CreateAsync(new CreateCardRequest("A only", "VISA", "1234", null), default)).Error); // the same card is free in B
        Assert.NotEqual(aCard.Id, (await b.Handler.ResolveOrCreateAsync("VISA", "1234", null, default))?.CardId);
    }

    [Fact]
    public async Task Contributor_HasWipesAndExports_TheHouseholdsCards()
    {
        var c = await ContextAsync();
        await c.Handler.CreateAsync(new CreateCardRequest("Main", "VISA", "1234", null), default);
        await c.Handler.ResolveChosenAsync((await c.Db.Cards.FirstAsync()).Id, "XXXXXXXXXXX8755X", default); // #210: a remembered pattern goes too
        var contributor = new CardDataContributor(new EfRepository<Card>(c.Db), new EfRepository<CardIdentity>(c.Db), new EfRepository<CardPattern>(c.Db));

        Assert.True(await contributor.HasDataAsync(c.Tenant));
        Assert.NotNull(await contributor.ExportAsync(c.Tenant));
        await contributor.WipeAsync(c.Tenant);
        Assert.False(await contributor.HasDataAsync(c.Tenant));
        Assert.Equal(0, await c.Db.Cards.IgnoreQueryFilters().CountAsync(x => x.TenantId == c.Tenant));
    }
}
