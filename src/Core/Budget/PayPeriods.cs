namespace Vuelto.Core.Budget;

/// <summary>
/// INCOME-1 (ADR-V023): how often an income line is paid. The month's plan is derived from it when the month is
/// created — <see cref="Weekly"/> × the month's week count, <see cref="Biweekly"/> × the pay days inside the month's
/// window, <see cref="Monthly"/> × 1 (see <see cref="IncomeSnapshot"/>). Stored lower-case.
/// </summary>
public static class PayPeriods
{
    public const string Weekly = "weekly";
    public const string Biweekly = "biweekly";
    public const string Monthly = "monthly";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Weekly, Biweekly, Monthly };

    /// <summary>A biweekly line's pay days when none are given: the 15th and the month's last day.</summary>
    public const int DefaultFirstPayDay = 15;
    public const int LastDayOfMonth = 31;

    /// <summary>Normalizes input to a stored code, or null when unknown.</summary>
    public static string? Normalize(string? value)
    {
        var lower = value?.Trim().ToLowerInvariant();
        return lower is not null && All.Contains(lower) ? lower : null;
    }
}
