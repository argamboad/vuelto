namespace Vuelto.Core.Budget;

/// <summary>The household's card for what a voucher printed, and what kind of plastic it is (CARDS-1/3).</summary>
public sealed record CardResolution(Guid CardId, string Kind)
{
    /// <summary>The payment method a transaction through this card belongs to — debit spends the account.</summary>
    public string PaymentMethod => CardKinds.PaymentMethod(Kind);
}

/// <summary>
/// The Cards slice's face for other slices (R7 — slices never reference each other): the review queue hands
/// over what the voucher printed and gets back the household's card for it, created on first sight with the
/// automatic alias (CARDS-1) and the kind the text named, or credit. Null when the text carries no card number.
/// </summary>
public interface ICardResolver
{
    /// <summary>
    /// The card a voucher's text names: by brand + last four (created on first sight), or — when the digits are not
    /// the last four (a pattern, #210) — the card the household mapped that pattern to, never a guess; null when
    /// there is no card number or an unmapped pattern.
    /// </summary>
    Task<CardResolution?> ResolveOrCreateAsync(string? brand, string? cardNumber, Guid? bankId, string? kind = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// The card the household picked for a voucher (#210): an active card of this household (null otherwise). When the
    /// voucher's number is a pattern, the answer is remembered for the next voucher that prints it — a different
    /// earlier answer is replaced.
    /// </summary>
    Task<CardResolution?> ResolveChosenAsync(Guid cardId, string? cardNumber, CancellationToken cancellationToken = default);

    /// <summary>The cards these patterns were mapped to — for the review queue's preselection (#210).</summary>
    Task<IReadOnlyDictionary<string, Guid>> KnownPatternsAsync(IReadOnlyCollection<string> patterns, CancellationToken cancellationToken = default);
}
