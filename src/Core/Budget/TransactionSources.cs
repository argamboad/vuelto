namespace Vuelto.Core.Budget;

/// <summary>
/// Where a transaction came from. Only <see cref="Manual"/> rows are editable through the transaction
/// API; <see cref="Email"/> rows are booked by the voucher review queue (P10) and
/// <see cref="RefundRealization"/> rows are derived inflows owned by their refund (P5b).
/// </summary>
public static class TransactionSources
{
    public const string Manual = "manual";
    public const string Email = "email";
    public const string RefundRealization = "refund_realization";

    /// <summary>Whether a row with this source may be edited or deleted directly.</summary>
    public static bool IsEditable(string source) => string.Equals(source, Manual, StringComparison.Ordinal);
}
