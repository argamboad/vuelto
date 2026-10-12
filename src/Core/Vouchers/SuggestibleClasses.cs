using Vuelto.Core.Budget;

namespace Vuelto.Core.Vouchers;

/// <summary>
/// The transaction classes a merchant rule may suggest and a voucher may be confirmed as (EMAIL-5/6):
/// the three spending classes. Inflows and envelope contributions are never what a bank voucher is.
/// </summary>
public static class SuggestibleClasses
{
    public const string Default = TransactionTypes.Budgeted;

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        TransactionTypes.Budgeted, TransactionTypes.Extraordinary, TransactionTypes.UnplannedEssential,
    };

    /// <summary>Normalizes user input to a stored code: null/blank ⇒ <c>null</c> ("no preference"), a known class ⇒ its code, anything else ⇒ <c>false</c>.</summary>
    public static bool TryNormalize(string? value, out string? normalized)
    {
        if (string.IsNullOrWhiteSpace(value)) { normalized = null; return true; }
        var lower = value.Trim().ToLowerInvariant();
        if (All.Contains(lower)) { normalized = lower; return true; }
        normalized = null;
        return false;
    }
}
