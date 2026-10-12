namespace Vuelto.Core.Budget;

/// <summary>
/// Where a budget month begins (ADR-V005). Stored values; display labels live in the RCL's resx.
/// </summary>
public static class MonthAnchors
{
    /// <summary>The last occurrence of the week-start weekday in the previous calendar month — suits a weekly pay cycle (default).</summary>
    public const string LastWeekdayPrev = "last_weekday_prev";

    /// <summary>The first occurrence of the week-start weekday in the calendar month.</summary>
    public const string FirstWeekdayCurrent = "first_weekday_current";

    /// <summary>The 1st of the calendar month, regardless of weekday — suits a monthly pay cycle.</summary>
    public const string FirstOfMonth = "first_of_month";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        LastWeekdayPrev, FirstWeekdayCurrent, FirstOfMonth,
    };
}
