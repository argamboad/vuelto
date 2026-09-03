namespace Vuelto.Core.Budget;

/// <summary>How a transaction was paid (ADR-V007): on the transaction, default credit card. Stored lower-case.</summary>
public static class PaymentMethods
{
    public const string CreditCard = "credit_card";
    public const string BankAccount = "bank_account";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { CreditCard, BankAccount };

    /// <summary>Normalizes user input to a stored code (null/blank ⇒ the credit-card default), or null when unknown.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return CreditCard;
        var lower = value.Trim().ToLowerInvariant();
        return All.Contains(lower) ? lower : null;
    }
}
