namespace Vuelto.Core.Budget;

/// <summary>
/// INCOME-1 (ADR-V023): whether an income line's amount is what arrives (<see cref="Fixed"/>) or an estimate the
/// household corrects on the month once the real figure is known (<see cref="Variable"/>). Stored lower-case.
/// </summary>
public static class IncomeKinds
{
    public const string Fixed = "fixed";
    public const string Variable = "variable";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Fixed, Variable };

    /// <summary>Normalizes input to a stored code, or null when unknown.</summary>
    public static string? Normalize(string? value)
    {
        var lower = value?.Trim().ToLowerInvariant();
        return lower is not null && All.Contains(lower) ? lower : null;
    }
}
