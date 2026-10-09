namespace Vuelto.Core.Budget;

/// <summary>
/// The five transaction classes (ADR-V007). The first three count as expenses; <see cref="Inflow"/>
/// folds into income and <see cref="EnvelopeContribution"/> is carved out of both. Stored lower-case.
/// </summary>
public static class TransactionTypes
{
    public const string Budgeted = "budgeted";
    public const string Extraordinary = "extraordinary";
    public const string UnplannedEssential = "unplanned_essential";
    public const string Inflow = "inflow";
    public const string EnvelopeContribution = "envelope_contribution";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Budgeted, Extraordinary, UnplannedEssential, Inflow, EnvelopeContribution,
    };

    /// <summary>The classes that count as spending.</summary>
    public static readonly IReadOnlySet<string> Expenses = new HashSet<string>(StringComparer.Ordinal)
    {
        Budgeted, Extraordinary, UnplannedEssential,
    };

    /// <summary>
    /// The classes a transaction may expect a refund on (ADR-V025): unplanned essentials and discretionary spend.
    /// Not <see cref="Budgeted"/> — you don't budget for money you expect back — and never money in or set aside.
    /// </summary>
    public static readonly IReadOnlySet<string> RefundClasses = new HashSet<string>(StringComparer.Ordinal)
    {
        Extraordinary, UnplannedEssential,
    };

    public static bool CanCarryRefund(string? type) => type is not null && RefundClasses.Contains(type);

    /// <summary>Normalizes user input to a stored code, or null when it is not a class.</summary>
    public static string? Normalize(string? value)
    {
        var lower = value?.Trim().ToLowerInvariant();
        return lower is not null && All.Contains(lower) ? lower : null;
    }
}
