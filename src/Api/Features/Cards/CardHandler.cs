using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;
using CardIdentity = Vuelto.Core.Entities.CardIdentity; // the entity (both namespaces are imported; the alias wins the lookup)
using Identity = Vuelto.Core.Budget.CardIdentity;         // the static text helper of the same name

namespace Vuelto.Api.Features.Cards;

/// <summary>
/// CARDS-1 (ADR-V021): the household's payment cards. A catalog like banks (alias unique case-insensitively, soft
/// delete, 409 reactivation offer, uniform 404) with one more identity — brand + last four, unique too — and one
/// more entry point: <see cref="ResolveOrCreateAsync"/>, what a voucher confirm calls with the text the bank
/// printed, creating the card as <c>VISA-1234</c> on first sight. Never seeded. <c>Query()</c> is tenant-filtered.
/// </summary>
public sealed class CardHandler(IRepository<Card> cards, IRepository<CardIdentity> identities, IRepository<Transaction> transactions, IRepository<Bank> banks, ICurrentTenant tenant, TimeProvider clock) : ICardResolver
{
    public async Task<IReadOnlyList<CardResponse>?> ListAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is null) return null;
        var query = includeInactive ? cards.Query() : cards.Query().Where(c => c.IsActive);
        var rows = await query.OrderBy(c => c.Name).ToListAsync(cancellationToken);
        var known = await IdentitiesAsync(cancellationToken);
        return rows.Select(c => CardResponse.From(c, known.GetValueOrDefault(c.Id))).ToList();
    }

    public async Task<(CardResponse? Card, ErrorResponse? Error)> CreateAsync(CreateCardRequest r, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId) return (null, NoTenant());
        if (string.IsNullOrWhiteSpace(r.Name)) return (null, Invalid("name is required"));
        if (r.Name.Trim().Length > 100) return (null, Invalid("name must be 100 characters or fewer"));
        if (Identity.Last4(r.Last4) is not { } last4) return (null, Invalid("last4 must carry the card's last four digits"));
        var brand = Identity.NormalizeBrand(r.Brand);
        if (brand.Length > 20) return (null, Invalid("brand must be 20 characters or fewer"));
        if (CardKinds.Normalize(r.Kind) is not { } kind) return (null, Invalid($"kind must be one of: {string.Join(", ", CardKinds.All)}"));
        if (r.BankId is { } bankId && !await banks.Query().AnyAsync(b => b.Id == bankId && b.IsActive, cancellationToken)) return (null, Invalid("unknown or inactive bank"));

        var name = r.Name.Trim();
        if (await FindByNameAsync(name, cancellationToken) is { } clash) return (null, Conflict(clash, "alias"));
        if (await FindByIdentityAsync(brand, last4, cancellationToken) is { } sameId && await cards.Query().FirstAsync(c => c.Id == sameId, cancellationToken) is { } same) return (null, Conflict(same, "card"));

        var now = clock.GetUtcNow();
        var card = new Card { TenantId = tenantId, Name = name, Brand = brand, Last4 = last4, BankId = r.BankId, Kind = kind, AutoNamed = false, IsActive = true, CreatedAt = now, UpdatedAt = now };
        await cards.AddAsync(card, cancellationToken);
        await identities.AddAsync(new CardIdentity { TenantId = tenantId, CardId = card.Id, Brand = brand, Last4 = last4, CreatedAt = now }, cancellationToken);
        await cards.SaveChangesAsync(cancellationToken);
        return (CardResponse.From(card, [new(brand, last4)]), null);
    }

    public async Task<(CardResponse? Card, ErrorResponse? Error)> UpdateAsync(Guid id, UpdateCardRequest r, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is null) return (null, NoTenant());
        if (string.IsNullOrWhiteSpace(r.Name)) return (null, Invalid("name is required"));
        if (r.Name.Trim().Length > 100) return (null, Invalid("name must be 100 characters or fewer"));

        var card = await cards.Query().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (card is null) return (null, NotFound());
        if (r.BankId is { } bankId && !await banks.Query().AnyAsync(b => b.Id == bankId && b.IsActive, cancellationToken)) return (null, Invalid("unknown or inactive bank"));

        var name = r.Name.Trim();
        if (await FindByNameAsync(name, cancellationToken) is { } clash && clash.Id != id) return (null, Conflict(clash, "alias"));

        if (CardKinds.Normalize(r.Kind ?? card.Kind) is not { } kind) return (null, Invalid($"kind must be one of: {string.Join(", ", CardKinds.All)}"));

        if (!string.Equals(card.Name, name, StringComparison.Ordinal)) card.AutoNamed = false; // a rename is the household's word from now on
        card.Name = name;
        card.BankId = r.BankId;
        card.IsActive = r.IsActive;
        card.Kind = kind;
        card.UpdatedAt = clock.GetUtcNow();
        cards.Update(card);

        // CARDS-3: the card says how money leaves; correcting its past rows is opt-in, because a card
        // flipped to debit today does not necessarily mean last year's purchases were mis-booked.
        int? backfilled = null;
        if (r.BackfillPaymentMethod)
        {
            var method = CardKinds.PaymentMethod(kind);
            backfilled = await transactions.Query()
                .Where(t => t.CardId == card.Id && t.PaymentMethod != method)
                .ExecuteUpdateAsync(u => u.SetProperty(t => t.PaymentMethod, method).SetProperty(t => t.UpdatedAt, card.UpdatedAt), cancellationToken);
        }

        await cards.SaveChangesAsync(cancellationToken);
        return (CardResponse.From(card, (await IdentitiesAsync(cancellationToken)).GetValueOrDefault(card.Id), backfilled), null);
    }

    /// <summary>
    /// A renewed card: the bank printed a new number, the household says "same card". Everything of <paramref name="id"/>
    /// moves under <paramref name="into"/> — its identities (so the next voucher matches the survivor), its transactions
    /// (history stays whole) — and the duplicate row goes. The survivor shows the newest number. 404 when either card is
    /// unknown or foreign; 400 when they are the same card.
    /// </summary>
    public async Task<(CardResponse? Card, ErrorResponse? Error)> MergeAsync(Guid id, Guid into, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is null) return (null, NoTenant());
        if (id == into) return (null, Invalid("a card cannot be merged into itself"));
        var source = await cards.Query().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        var target = await cards.Query().FirstOrDefaultAsync(c => c.Id == into, cancellationToken);
        if (source is null || target is null) return (null, NotFound());

        var now = clock.GetUtcNow();
        await identities.Query().Where(i => i.CardId == id).ExecuteUpdateAsync(u => u.SetProperty(i => i.CardId, into), cancellationToken);
        await transactions.Query().Where(t => t.CardId == id).ExecuteUpdateAsync(u => u.SetProperty(t => t.CardId, into).SetProperty(t => t.UpdatedAt, now), cancellationToken);
        if (source.CreatedAt >= target.CreatedAt) { target.Brand = source.Brand; target.Last4 = source.Last4; } // the newest number is the one on the plastic; on a tie the card being folded in is the newcomer
        target.BankId ??= source.BankId;
        target.UpdatedAt = now;
        cards.Update(target);
        cards.Remove(source);
        await cards.SaveChangesAsync(cancellationToken);
        return (CardResponse.From(target, (await IdentitiesAsync(cancellationToken)).GetValueOrDefault(target.Id)), null);
    }

    /// <summary>
    /// The voucher path: the card the text identifies, found by brand + last four (any state — an inactive card
    /// still labels history) or created as <c>BRAND-1234</c>. A first-sight race on the unique index is absorbed
    /// by re-reading. Null when the text carries no card number.
    /// </summary>
    public async Task<CardResolution?> ResolveOrCreateAsync(string? brand, string? cardNumber, Guid? bankId, string? kind = null, CancellationToken cancellationToken = default)
    {
        if (tenant.TenantId is not { } tenantId) return null;
        if (Identity.Parse(brand, cardNumber) is not var (b, last4)) return null;

        if (await FindByIdentityAsync(b, last4, cancellationToken) is { } existing) return await ResolutionAsync(existing, cancellationToken);
        if (await ReconcileBrandAsync(b, last4, cancellationToken) is { } sameCard) return await ResolutionAsync(sameCard, cancellationToken);

        var now = clock.GetUtcNow();
        var name = Identity.AutoName(b, last4);
        if (await FindByNameAsync(name, cancellationToken) is not null) name = $"{name} ({last4})"; // the alias is taken by another card — keep the identity, vary the alias
        // The voucher's word only ever sets the kind at creation; an existing card keeps whatever the household chose.
        var card = new Card { TenantId = tenantId, Name = name, Brand = b, Last4 = last4, BankId = bankId, Kind = CardKinds.Normalize(kind) ?? CardKinds.Credit, AutoNamed = true, IsActive = true, CreatedAt = now, UpdatedAt = now };
        var identity = new CardIdentity { TenantId = tenantId, CardId = card.Id, Brand = b, Last4 = last4, CreatedAt = now };
        await cards.AddAsync(card, cancellationToken);
        await identities.AddAsync(identity, cancellationToken);
        try
        {
            await cards.SaveChangesAsync(cancellationToken);
            return new CardResolution(card.Id, card.Kind);
        }
        catch (DbUpdateException)
        {
            identities.Remove(identity); cards.Remove(card); // Added → Detached: a concurrent confirm created it first
            return await FindByIdentityAsync(b, last4, cancellationToken) is { } winner ? await ResolutionAsync(winner, cancellationToken) : null;
        }
    }

    private async Task<CardResolution?> ResolutionAsync(Guid cardId, CancellationToken cancellationToken) =>
        await cards.Query().Where(c => c.Id == cardId).Select(c => new CardResolution(c.Id, c.Kind)).FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Vouchers do not all print the brand (BN payments say "TARJETA DE CREDITO"; drafts staged before brand capture
    /// carry none). A brandless number is the card already known by those four digits; a branded number that meets
    /// a <c>CARD</c> placeholder upgrades it in place — the identity, the card's display brand and a still-automatic
    /// alias (<c>CARD-1966</c> → <c>VISA-1966</c>) — so one piece of plastic never becomes two cards.
    /// </summary>
    private async Task<Guid?> ReconcileBrandAsync(string brand, string last4, CancellationToken cancellationToken)
    {
        if (brand == Identity.UnknownBrand)
            return (await identities.Query().Where(i => i.Last4 == last4).OrderBy(i => i.CreatedAt).FirstOrDefaultAsync(cancellationToken))?.CardId;

        var placeholder = await identities.Query().FirstOrDefaultAsync(i => i.Last4 == last4 && i.Brand == Identity.UnknownBrand, cancellationToken);
        if (placeholder is null) return null;

        var card = await cards.Query().FirstAsync(c => c.Id == placeholder.CardId, cancellationToken);
        placeholder.Brand = brand;
        identities.Update(placeholder);
        if (card.Brand == Identity.UnknownBrand && card.Last4 == last4) card.Brand = brand;
        if (card.AutoNamed && card.Name == Identity.AutoName(Identity.UnknownBrand, last4))
        {
            var upgraded = Identity.AutoName(brand, last4);
            if (await FindByNameAsync(upgraded, cancellationToken) is null) card.Name = upgraded;
        }
        card.UpdatedAt = clock.GetUtcNow();
        cards.Update(card);
        await cards.SaveChangesAsync(cancellationToken);
        return card.Id;
    }

    private async Task<Dictionary<Guid, List<CardIdentityResponse>>> IdentitiesAsync(CancellationToken cancellationToken) =>
        (await identities.Query().OrderBy(i => i.CreatedAt).ToListAsync(cancellationToken))
            .GroupBy(i => i.CardId)
            .ToDictionary(g => g.Key, g => g.Select(i => new CardIdentityResponse(i.Brand, i.Last4)).ToList());

    private Task<Card?> FindByNameAsync(string name, CancellationToken cancellationToken)
    {
        var lowered = name.ToLowerInvariant();
        return cards.Query().FirstOrDefaultAsync(c => c.Name.ToLower() == lowered, cancellationToken);
    }

    /// <summary>The card a (brand, last four) pair names — through the identity table, so a renewed card's old number still finds it.</summary>
    private async Task<Guid?> FindByIdentityAsync(string brand, string last4, CancellationToken cancellationToken) =>
        (await identities.Query().FirstOrDefaultAsync(i => i.Brand == brand && i.Last4 == last4, cancellationToken))?.CardId;

    private static ErrorResponse Conflict(Card existing, string what) => existing.IsActive
        ? new CardConflictResponse("card_exists", $"A card with that {what} already exists: '{existing.Name}' ({existing.Brand} ····{existing.Last4})", existing.Id, existing.Name)
        : new CardConflictResponse("card_exists_inactive", $"'{existing.Name}' ({existing.Brand} ····{existing.Last4}) already exists but is inactive — reactivate it?", existing.Id, existing.Name);

    private static ErrorResponse Invalid(string message) => new("invalid_request", message);
    private static ErrorResponse NotFound() => new("not_found", "card not found");
    private static ErrorResponse NoTenant() => new("invalid_token", "No household on the token");
}
