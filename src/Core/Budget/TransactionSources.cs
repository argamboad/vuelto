namespace Vuelto.Core.Budget;

/// <summary>
/// Where a transaction came from. <see cref="Manual"/> rows and <see cref="Email"/> rows (booked by the
/// voucher review queue, EMAIL-6 — the user's own confirmed data) are editable through the transaction
/// API; <see cref="RefundRealization"/> rows are derived inflows owned by their refund (P5b) and are not.
/// </summary>
public static class TransactionSources
{
    public const string Manual = "manual";
    public const string Email = "email";
    public const string RefundRealization = "refund_realization";

    /// <summary>Whether a row with this source may be edited or deleted directly.</summary>
    public static bool IsEditable(string source) => !string.Equals(source, RefundRealization, StringComparison.Ordinal);
}
