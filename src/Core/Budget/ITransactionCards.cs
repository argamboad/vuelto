namespace Vuelto.Core.Budget;

/// <summary>
/// The Ledger slice's face for what a card change does to the household's transactions (Arch A8, R162 — the Ledger
/// writes transactions; the Cards slice goes through here). Both are set-based and immediate, scoped to the current
/// household, and return the number of transactions changed.
/// </summary>
public interface ITransactionCards
{
    /// <summary>A renewed card folded into the original: its transactions move to <paramref name="intoCardId"/> (CARDS-1).</summary>
    Task<int> MoveAsync(Guid fromCardId, Guid intoCardId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>The card's kind changed and the household opted to correct its past rows (CARDS-3).</summary>
    Task<int> SetPaymentMethodAsync(Guid cardId, string paymentMethod, DateTimeOffset now, CancellationToken cancellationToken = default);
}
