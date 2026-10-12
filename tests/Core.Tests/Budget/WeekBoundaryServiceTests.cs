using Vuelto.Core.Budget;

namespace Vuelto.Core.Tests.Budget;

/// <summary>
/// Ported verbatim from the donor (US-003 / US-004 / US-015): Week-1 start for all three anchors,
/// week tiling (4 or 5 weeks, last week clamped to the window), and date → budget-month resolution.
/// Weekday numbering: 0 = Sunday … 6 = Saturday (4 = Thursday, the default).
/// Reference calendar: Jun 2026 starts Monday; May 2026 starts Friday; Dec 2025 starts Monday.
/// </summary>
public class WeekBoundaryServiceTests
{
    private readonly WeekBoundaryService _service = new();

    [Theory]
    [InlineData(2026, 6, 4, 2026, 5, 28)]   // last Thursday of May 2026
    [InlineData(2026, 6, 1, 2026, 5, 25)]   // last Monday of May 2026
    [InlineData(2026, 6, 0, 2026, 5, 31)]   // last Sunday of May 2026 = its last day
    [InlineData(2026, 1, 4, 2025, 12, 25)]  // year boundary: last Thursday of Dec 2025
    [InlineData(2026, 3, 6, 2026, 2, 28)]   // last Saturday of Feb 2026 (non-leap)
    public void GetWeek1StartDate_LastWeekdayPrev(int year, int month, int weekday, int expYear, int expMonth, int expDay)
    {
        var result = _service.GetWeek1StartDate(year, month, weekday, MonthAnchors.LastWeekdayPrev);
        Assert.Equal(new DateOnly(expYear, expMonth, expDay), result);
    }

    [Theory]
    [InlineData(2026, 6, 4, 2026, 6, 4)]    // first Thursday of June 2026
    [InlineData(2026, 6, 1, 2026, 6, 1)]    // month starts on the chosen weekday
    [InlineData(2026, 6, 0, 2026, 6, 7)]    // first Sunday of June 2026
    [InlineData(2026, 6, 6, 2026, 6, 6)]    // first Saturday of June 2026
    [InlineData(2026, 1, 4, 2026, 1, 1)]    // Jan 1 2026 is a Thursday
    public void GetWeek1StartDate_FirstWeekdayCurrent(int year, int month, int weekday, int expYear, int expMonth, int expDay)
    {
        var result = _service.GetWeek1StartDate(year, month, weekday, MonthAnchors.FirstWeekdayCurrent);
        Assert.Equal(new DateOnly(expYear, expMonth, expDay), result);
    }

    [Theory]
    [InlineData(2026, 6, 4)]
    [InlineData(2026, 6, 0)]
    [InlineData(2025, 12, 1)]
    public void GetWeek1StartDate_FirstOfMonth_AlwaysReturnsThe1st(int year, int month, int weekday)
    {
        var result = _service.GetWeek1StartDate(year, month, weekday, MonthAnchors.FirstOfMonth);
        Assert.Equal(new DateOnly(year, month, 1), result);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public void GetWeek1StartDate_InvalidWeekday_Throws(int weekday) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _service.GetWeek1StartDate(2026, 6, weekday, MonthAnchors.FirstOfMonth));

    [Fact]
    public void GetWeek1StartDate_UnknownAnchor_Throws() =>
        Assert.Throws<ArgumentException>(() => _service.GetWeek1StartDate(2026, 6, 4, "middle_of_month"));

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void GetWeek1StartDate_InvalidMonth_Throws(int month) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _service.GetWeek1StartDate(2026, month, 4, MonthAnchors.FirstOfMonth));

    [Fact]
    public void GenerateWeeks_June2026_Thursday_LastWeekdayPrev_Has4Weeks()
    {
        // Anchor May 28 (last Thu of May); July's anchor is Jun 25
        var weeks = _service.GenerateWeeks(2026, 6, 4, MonthAnchors.LastWeekdayPrev);

        Assert.Equal(4, weeks.Count);
        Assert.Equal(new DateOnly(2026, 5, 28), weeks[0].StartDate);
        Assert.Equal(new DateOnly(2026, 6, 3), weeks[0].EndDate);
        Assert.Equal(new DateOnly(2026, 6, 18), weeks[3].StartDate);
        Assert.Equal(new DateOnly(2026, 6, 24), weeks[3].EndDate);
    }

    [Fact]
    public void GenerateWeeks_July2026_Thursday_LastWeekdayPrev_Has5Weeks()
    {
        // Anchor Jun 25; August's anchor is Jul 30 — a 35-day window
        var weeks = _service.GenerateWeeks(2026, 7, 4, MonthAnchors.LastWeekdayPrev);

        Assert.Equal(5, weeks.Count);
        Assert.Equal(new DateOnly(2026, 6, 25), weeks[0].StartDate);
        Assert.Equal(new DateOnly(2026, 7, 23), weeks[4].StartDate);
        Assert.Equal(new DateOnly(2026, 7, 29), weeks[4].EndDate);
    }

    [Fact]
    public void GenerateWeeks_June2026_FirstOfMonth_Has5Weeks_LastWeekClamped()
    {
        var weeks = _service.GenerateWeeks(2026, 6, 4, MonthAnchors.FirstOfMonth);

        Assert.Equal(5, weeks.Count);
        Assert.Equal(new DateOnly(2026, 6, 1), weeks[0].StartDate);
        Assert.Equal(new DateOnly(2026, 6, 29), weeks[4].StartDate);
        Assert.Equal(new DateOnly(2026, 6, 30), weeks[4].EndDate); // clamped to the window end
    }

    [Fact]
    public void GenerateWeeks_February2026_FirstOfMonth_HasExactly4Weeks()
    {
        var weeks = _service.GenerateWeeks(2026, 2, 4, MonthAnchors.FirstOfMonth);

        Assert.Equal(4, weeks.Count);
        Assert.Equal(new DateOnly(2026, 2, 1), weeks[0].StartDate);
        Assert.Equal(new DateOnly(2026, 2, 28), weeks[3].EndDate);
    }

    [Fact]
    public void GenerateWeeks_December2026_Thursday_LastWeekdayPrev_CrossesYearBoundary()
    {
        // Anchor Nov 26; January 2027's anchor is Dec 31 — 5 weeks
        var weeks = _service.GenerateWeeks(2026, 12, 4, MonthAnchors.LastWeekdayPrev);

        Assert.Equal(5, weeks.Count);
        Assert.Equal(new DateOnly(2026, 11, 26), weeks[0].StartDate);
        Assert.Equal(new DateOnly(2026, 12, 24), weeks[4].StartDate);
        Assert.Equal(new DateOnly(2026, 12, 30), weeks[4].EndDate);
    }

    [Fact]
    public void GenerateWeeks_June2026_Monday_FirstWeekdayCurrent_Has5Weeks()
    {
        // First Mon Jun 1; July's first Mon is Jul 6 — 35-day window
        var weeks = _service.GenerateWeeks(2026, 6, 1, MonthAnchors.FirstWeekdayCurrent);

        Assert.Equal(5, weeks.Count);
        Assert.Equal(new DateOnly(2026, 6, 1), weeks[0].StartDate);
        Assert.Equal(new DateOnly(2026, 6, 29), weeks[4].StartDate);
    }

    [Theory]
    [InlineData(2026, 6, 4, MonthAnchors.LastWeekdayPrev)]
    [InlineData(2026, 7, 4, MonthAnchors.LastWeekdayPrev)]
    [InlineData(2026, 2, 0, MonthAnchors.FirstOfMonth)]
    [InlineData(2026, 12, 1, MonthAnchors.FirstWeekdayCurrent)]
    public void GenerateWeeks_Invariants_ContiguousSevenDayNumberedWeeks(int year, int month, int weekday, string anchor)
    {
        var weeks = _service.GenerateWeeks(year, month, weekday, anchor);

        Assert.InRange(weeks.Count, 4, 5);
        for (var i = 0; i < weeks.Count; i++)
        {
            Assert.Equal(i + 1, weeks[i].WeekNumber);
            Assert.Equal(weeks[i].StartDate.AddDays(6), weeks[i].EndDate);
            if (i > 0) Assert.Equal(weeks[i - 1].EndDate.AddDays(1), weeks[i].StartDate);
        }
        Assert.Equal(_service.GetWeek1StartDate(year, month, weekday, anchor), weeks[0].StartDate);
    }

    // US-015 AC2: any date maps to exactly one budget month — used to auto-create the month a
    // transaction's date falls into.
    [Theory]
    [InlineData(2026, 5, 30, MonthAnchors.LastWeekdayPrev, 2026, 6)]   // June's window, previous calendar month
    [InlineData(2026, 6, 5, MonthAnchors.LastWeekdayPrev, 2026, 6)]
    [InlineData(2026, 6, 24, MonthAnchors.LastWeekdayPrev, 2026, 6)]   // last day of June's window
    [InlineData(2026, 6, 25, MonthAnchors.LastWeekdayPrev, 2026, 7)]   // July's anchor day
    [InlineData(2026, 7, 29, MonthAnchors.LastWeekdayPrev, 2026, 7)]
    [InlineData(2026, 7, 30, MonthAnchors.LastWeekdayPrev, 2026, 8)]
    [InlineData(2026, 12, 31, MonthAnchors.LastWeekdayPrev, 2027, 1)]  // year rollover
    [InlineData(2026, 6, 1, MonthAnchors.FirstOfMonth, 2026, 6)]
    [InlineData(2026, 6, 2, MonthAnchors.FirstWeekdayCurrent, 2026, 5)] // before June's first Thursday → May
    public void GetBudgetMonthForDate_ResolvesByAnchorWindow(int year, int month, int day, string anchor, int expectedYear, int expectedMonth)
    {
        var (resolvedYear, resolvedMonth) = _service.GetBudgetMonthForDate(new DateOnly(year, month, day), 4, anchor);

        Assert.Equal(expectedYear, resolvedYear);
        Assert.Equal(expectedMonth, resolvedMonth);
    }
}
