namespace Vuelto.Core.Budget;

/// <summary>
/// The Cards slice's face for other slices (R7 — slices never reference each other): the review queue hands
/// over what the voucher printed and gets back the household's card for it, created on first sight with the
/// automatic alias (CARDS-1). Null when the text carries no card number.
/// </summary>
public interface ICardResolver
{
    Task<Guid?> ResolveOrCreateAsync(string? brand, string? cardNumber, Guid? bankId, CancellationToken cancellationToken = default);
}
