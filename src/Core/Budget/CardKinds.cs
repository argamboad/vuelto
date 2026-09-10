namespace Vuelto.Core.Budget;

/// <summary>
/// Credit or debit (CARDS-3): a property of the plastic, not of each purchase, so it lives on the card and
/// every transaction through it inherits the right <see cref="PaymentMethods"/>. A debit card spends from
/// the bank account, so its purchases are <c>bank_account</c> while still naming the card. Default credit —
/// every card that existed before this slice, and every voucher whose text does not say.
/// </summary>
public static class CardKinds
{
    public const string Credit = "credit";
    public const string Debit = "debit";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Credit, Debit };

    /// <summary>Normalizes user input to a stored code (null/blank ⇒ credit), or null when unknown.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Credit;
        var lower = value.Trim().ToLowerInvariant();
        return All.Contains(lower) ? lower : null;
    }

    /// <summary>How money leaves through a card of this kind: debit spends the account, credit spends the card.</summary>
    public static string PaymentMethod(string? kind) =>
        string.Equals(kind, Debit, StringComparison.Ordinal) ? PaymentMethods.BankAccount : PaymentMethods.CreditCard;
}
