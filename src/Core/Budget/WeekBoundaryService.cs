namespace Vuelto.Core.Budget;

/// <inheritdoc cref="IWeekBoundaryService"/>
public class WeekBoundaryService : IWeekBoundaryService
{
    private const int MaxWeeks = 5;

    public DateOnly GetWeek1StartDate(int year, int month, int weekStartWeekday, string monthAnchor)
    {
        if (month is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(month), month, "Month must be 1-12");
        if (weekStartWeekday is < 0 or > 6)
            throw new ArgumentOutOfRangeException(nameof(weekStartWeekday), weekStartWeekday, "Weekday must be 0 (Sunday) - 6 (Saturday)");

        return monthAnchor switch
        {
            MonthAnchors.LastWeekdayPrev => LastOccurrenceInPreviousMonth(year, month, weekStartWeekday),
            MonthAnchors.FirstWeekdayCurrent => FirstOccurrenceInMonth(year, month, weekStartWeekday),
            MonthAnchors.FirstOfMonth => new DateOnly(year, month, 1),
            _ => throw new ArgumentException($"Unknown month anchor '{monthAnchor}'", nameof(monthAnchor)),
        };
    }

    public IReadOnlyList<WeekBoundary> GenerateWeeks(int year, int month, int weekStartWeekday, string monthAnchor)
    {
        var anchor = GetWeek1StartDate(year, month, weekStartWeekday, monthAnchor);
        var (nextYear, nextMonth) = month == 12 ? (year + 1, 1) : (year, month + 1);
        var nextAnchor = GetWeek1StartDate(nextYear, nextMonth, weekStartWeekday, monthAnchor);

        // The window [anchor, nextAnchor) isn't always a multiple of 7 (first_of_month): clamp the
        // final week to the day before the next anchor so the weeks tile the window exactly.
        var windowEnd = nextAnchor.AddDays(-1);
        var weeks = new List<WeekBoundary>();
        for (var start = anchor; start < nextAnchor && weeks.Count < MaxWeeks; start = start.AddDays(7))
        {
            var end = start.AddDays(6);
            if (end > windowEnd) end = windowEnd;
            weeks.Add(new WeekBoundary(weeks.Count + 1, start, end));
        }
        return weeks;
    }

    public (int Year, int Month) GetBudgetMonthForDate(DateOnly date, int weekStartWeekday, string monthAnchor)
    {
        // A window runs [anchor(m), anchor(m+1)) and an anchor never sits more than one calendar
        // month away from m, so the budget month is the next, current or previous calendar month —
        // whichever has the greatest anchor <= date. Check from the latest candidate down.
        foreach (var candidate in new[] { date.AddMonths(1), date, date.AddMonths(-1) })
        {
            var anchor = GetWeek1StartDate(candidate.Year, candidate.Month, weekStartWeekday, monthAnchor);
            if (anchor <= date) return (candidate.Year, candidate.Month);
        }

        throw new InvalidOperationException($"No budget month window contains {date:yyyy-MM-dd} (anchor '{monthAnchor}')");
    }

    private static DateOnly LastOccurrenceInPreviousMonth(int year, int month, int weekday)
    {
        var lastOfPrevious = new DateOnly(year, month, 1).AddDays(-1);
        var daysBack = ((int)lastOfPrevious.DayOfWeek - weekday + 7) % 7;
        return lastOfPrevious.AddDays(-daysBack);
    }

    private static DateOnly FirstOccurrenceInMonth(int year, int month, int weekday)
    {
        var firstOfMonth = new DateOnly(year, month, 1);
        var daysForward = (weekday - (int)firstOfMonth.DayOfWeek + 7) % 7;
        return firstOfMonth.AddDays(daysForward);
    }
}
