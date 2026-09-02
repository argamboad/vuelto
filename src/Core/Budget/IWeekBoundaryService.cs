namespace Vuelto.Core.Budget;

/// <summary>
/// Computes week boundaries and month membership from a household's budget settings (ADR-V005).
/// Pure — no I/O, no clock. Ported from the donor (US-003 / US-004 / US-015).
/// </summary>
public interface IWeekBoundaryService
{
    /// <summary>
    /// The start date of Week 1 for a calendar (<paramref name="year"/>, <paramref name="month"/>).
    /// </summary>
    /// <param name="weekStartWeekday">0 = Sunday … 6 = Saturday.</param>
    /// <param name="monthAnchor">One of <see cref="MonthAnchors"/>.</param>
    DateOnly GetWeek1StartDate(int year, int month, int weekStartWeekday, string monthAnchor);

    /// <summary>
    /// The month's weeks: contiguous 7-day blocks from the month's anchor up to (not including) the
    /// next month's anchor, the last one clamped to the window. The count (4 or 5) is derived, never input.
    /// </summary>
    IReadOnlyList<WeekBoundary> GenerateWeeks(int year, int month, int weekStartWeekday, string monthAnchor);

    /// <summary>
    /// The budget month whose anchor window contains <paramref name="date"/> — by window, not
    /// calendar month (28 May belongs to June under "last Thursday of the previous month"). The
    /// windows partition the timeline, so every date maps to exactly one (year, month).
    /// </summary>
    (int Year, int Month) GetBudgetMonthForDate(DateOnly date, int weekStartWeekday, string monthAnchor);
}
