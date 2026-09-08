using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Features.Cards;
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
        return new Ctx(db, new CardHandler(new EfRepository<Card>(db), new EfRepository<CardIdentity>(db), new EfRepository<Transaction>(db), new EfRepository<Bank>(db), new TestCurrentTenant { TenantId = tenant }, new FakeTimeProvider(T0)), tenant, bank.Id);
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
        var bn = await c.Handler.ResolveOrCreateAsync(null, "XXXXXXXXXXX0000X", c.BankId, default);      // BN payment receipt: no brand label
        var none = await c.Handler.ResolveOrCreateAsync("VISA", null, c.BankId, default);

        Assert.NotNull(first);
        Assert.Equal(first, again);
        Assert.Null(none);
        var cards = (await c.Handler.ListAsync(true, default))!.OrderBy(x => x.Name).ToList();
        Assert.Equal(["CARD-0000", "VISA-1234"], cards.Select(x => x.Name));
        Assert.All(cards, x => Assert.True(x.AutoNamed));
        Assert.Equal(((Guid?)c.BankId, bn), (cards[0].BankId, (Guid?)cards[0].Id));
    }

    [Fact]
    public async Task Rename_ClearsTheAutoFlag_AndTheVoucherPathStillFindsTheCard()
    {
        var c = await ContextAsync();
        var id = (await c.Handler.ResolveOrCreateAsync("VISA", "************1234", null, default))!.Value;

        var (renamed, error) = await c.Handler.UpdateAsync(id, new UpdateCardRequest("Tarjeta de Allan", c.BankId, IsActive: true), default);

        Assert.Null(error);
        Assert.Equal(("Tarjeta de Allan", false, c.BankId), (renamed!.Name, renamed.AutoNamed, renamed.BankId));
        Assert.Equal(id, await c.Handler.ResolveOrCreateAsync("VISA", "************1234", null, default)); // identity, not alias, is the key
    }

    [Fact]
    public async Task ResolveOrCreate_ABrandlessNumber_IsTheCardAlreadyKnownByIt_AndABrandUpgradesAPlaceholder()
    {
        // A BN payment prints no brand; a draft staged before brand capture carries none. One plastic, one card.
        var c = await ContextAsync();
        var visa = (await c.Handler.ResolveOrCreateAsync("VISA", "************1234", c.BankId, default))!.Value;
        Assert.Equal(visa, await c.Handler.ResolveOrCreateAsync(null, "************1234", null, default)); // brandless → the Visa

        var placeholder = (await c.Handler.ResolveOrCreateAsync(null, "************1966", c.BankId, default))!.Value;
        Assert.Equal("CARD-1966", (await c.Handler.ListAsync(true, default))!.Single(x => x.Id == placeholder).Name);

        Assert.Equal(placeholder, await c.Handler.ResolveOrCreateAsync("VISA", "************1966", null, default)); // the brand arrives → same card, upgraded
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
        var original = (await c.Handler.ResolveOrCreateAsync("VISA", "************1234", c.BankId, default))!.Value;
        await c.Handler.UpdateAsync(original, new UpdateCardRequest("Allan's Visa", c.BankId, IsActive: true), default);
        var renewed = (await c.Handler.ResolveOrCreateAsync("VISA", "************5678", c.BankId, default))!.Value;
        var category = new Category { TenantId = c.Tenant, Name = "Groceries", CreatedAt = T0, UpdatedAt = T0 };
        var month = new Month { TenantId = c.Tenant, Year = 2026, MonthNumber = 9, WeekCount = 5, Week1StartDate = new DateOnly(2026, 8, 25), PrimaryIncomeCurrency = "USD", SecondaryIncomeCurrency = "USD", CreatedAt = T0, UpdatedAt = T0 };
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
        Assert.Equal(original, await c.Handler.ResolveOrCreateAsync("VISA", "************5678", null, default)); // the renewed number now finds the survivor
        Assert.Equal(original, await c.Handler.ResolveOrCreateAsync("VISA", "************1234", null, default));

        Assert.Equal("invalid_request", (await c.Handler.MergeAsync(original, original, default)).Error!.Error);
        Assert.Equal("not_found", (await c.Handler.MergeAsync(original, Guid.CreateVersion7(), default)).Error!.Error);
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
        Assert.NotEqual(aCard.Id, await b.Handler.ResolveOrCreateAsync("VISA", "1234", null, default));
    }

    [Fact]
    public async Task Contributor_HasWipesAndExports_TheHouseholdsCards()
    {
        var c = await ContextAsync();
        await c.Handler.CreateAsync(new CreateCardRequest("Main", "VISA", "1234", null), default);
        var contributor = new CardDataContributor(new EfRepository<Card>(c.Db), new EfRepository<CardIdentity>(c.Db));

        Assert.True(await contributor.HasDataAsync(c.Tenant));
        Assert.NotNull(await contributor.ExportAsync(c.Tenant));
        await contributor.WipeAsync(c.Tenant);
        Assert.False(await contributor.HasDataAsync(c.Tenant));
        Assert.Equal(0, await c.Db.Cards.IgnoreQueryFilters().CountAsync(x => x.TenantId == c.Tenant));
    }
}
