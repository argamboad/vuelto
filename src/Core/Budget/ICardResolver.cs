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
    Task<CardResolution?> ResolveOrCreateAsync(string? brand, string? cardNumber, Guid? bankId, string? kind = null, CancellationToken cancellationToken = default);
}
