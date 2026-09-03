namespace Vuelto.Core.Budget;

/// <summary>
/// When an envelope's dashboard reminder shows (ADR-V007, from donor ADR-0018): every month, or only
/// on 5-week months — the "extra paycheck" months where the household tops buckets up. Stored values
/// are the lower-case codes.
/// </summary>
public static class EnvelopeReminderCadences
{
    public const string Monthly = "monthly";
    public const string FiveWeekMonths = "five_week_months";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Monthly, FiveWeekMonths };

    /// <summary>Normalizes user input to a stored code, or null when it is not a supported cadence.</summary>
    public static string? Normalize(string? value)
    {
        var lower = value?.Trim().ToLowerInvariant();
        return lower is not null && All.Contains(lower) ? lower : null;
    }
}
