using Vuelto.Core.Budget;
using Vuelto.Core.Entities;

namespace Vuelto.Core.Tests.Budget;

/// <summary>
/// INCOME-1: how an income line becomes a new month's plan — weekly × the week count, biweekly × the pay days inside
/// the window (the 15th and the last day by default, clamped to short months), monthly × 1 — and which lines snapshot
/// (active ones, in list order, skipping a member who left the household).
/// </summary>
public class IncomeSnapshotTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Weeks of <paramref name="count"/> seven-day spans from <paramref name="start"/>.</summary>
    private static List<WeekBoundary> Weeks(DateOnly start, int count) =>
        Enumerable.Range(0, count).Select(i => new WeekBoundary(i + 1, start.AddDays(7 * i), start.AddDays(7 * i + 6))).ToList();

    private static readonly List<WeekBoundary> September2026 = Weeks(new DateOnly(2026, 8, 25), 5); // Aug 25 – Sep 28
    private static readonly List<WeekBoundary> October2026 = Weeks(new DateOnly(2026, 9, 29), 4);   // Sep 29 – Oct 26

    private static IncomeLine Line(string name, string period, decimal amount, int? day1 = null, int? day2 = null,
        Guid? member = null, bool active = true, int order = 0, string currency = "USD") => new()
    {
        TenantId = Tenant, Name = name, PayPeriod = period, Amount = amount, PayDay1 = day1, PayDay2 = day2,
        MemberUserId = member, IsActive = active, SortOrder = order, Currency = currency,
    };

    [Fact]
    public void Weekly_FollowsTheWeekCount()
    {
        var salary = Line("Allan salary", PayPeriods.Weekly, 500m);
        Assert.Equal(2_500m, IncomeSnapshot.PlannedAmount(salary, September2026));
        Assert.Equal(2_000m, IncomeSnapshot.PlannedAmount(salary, October2026));
    }

    [Fact]
    public void Monthly_IsTheSameEveryMonth()
    {
        var salary = Line("Son", PayPeriods.Monthly, 600_000m, currency: "CRC");
        Assert.Equal(600_000m, IncomeSnapshot.PlannedAmount(salary, September2026));
        Assert.Equal(600_000m, IncomeSnapshot.PlannedAmount(salary, October2026));
    }

    [Theory]
    [InlineData("2026-08-25", "2026-09-28", 2)] // Aug 31, Sep 15
    [InlineData("2026-02-12", "2026-03-18", 3)] // Feb 15, Feb 28 (the last day of a short month), Mar 15
    [InlineData("2026-06-01", "2026-06-30", 2)] // the whole calendar month
    [InlineData("2026-06-16", "2026-06-29", 0)] // between the two
    [InlineData("2026-12-20", "2027-01-20", 2)] // Dec 31, Jan 15 — across the year
    public void Biweekly_CountsThePayDaysInTheWindow(string from, string to, int expected)
    {
        Assert.Equal(expected, IncomeSnapshot.PayDaysIn(15, 31, DateOnly.Parse(from), DateOnly.Parse(to)));
    }

    [Fact]
    public void Biweekly_UsesTheLineDays_OrTheDefaults()
    {
        Assert.Equal(800_000m, IncomeSnapshot.PlannedAmount(Line("Q", PayPeriods.Biweekly, 400_000m), September2026)); // 15th + last
        // The 1st and the 16th: Sep 1 and Sep 16 fall inside Aug 25 – Sep 28; Aug 1 and Aug 16 are before it.
        Assert.Equal(800_000m, IncomeSnapshot.PlannedAmount(Line("Q", PayPeriods.Biweekly, 400_000m, 1, 16), September2026));
        // The 26th and the 28th: Aug 26, Aug 28, Sep 26, Sep 28.
        Assert.Equal(1_600_000m, IncomeSnapshot.PlannedAmount(Line("Q", PayPeriods.Biweekly, 400_000m, 26, 28), September2026));
    }

    [Fact]
    public void TheSameDayTwice_CountsOnce() =>
        Assert.Equal(1, IncomeSnapshot.PayDaysIn(31, 30, new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28))); // both clamp to Feb 28

    [Fact]
    public void AmountsRoundToCents() =>
        Assert.Equal(1_666.67m, IncomeSnapshot.PlannedAmount(Line("x", PayPeriods.Weekly, 333.333m), September2026));

    [Fact]
    public void NoWeeks_NoPlan() => Assert.Equal(0m, IncomeSnapshot.PlannedAmount(Line("x", PayPeriods.Weekly, 10m), []));

    [Fact]
    public void Rows_ActiveLinesInListOrder_SkippingSomeoneWhoLeft()
    {
        var stays = Guid.CreateVersion7();
        var left = Guid.CreateVersion7();
        var month = Guid.CreateVersion7();
        IncomeLine[] lines =
        [
            Line("Freelance", PayPeriods.Monthly, 300m, member: stays, order: 2),
            Line("Allan salary", PayPeriods.Weekly, 500m, member: stays, order: 0),
            Line("Old job", PayPeriods.Monthly, 100m, active: false, order: 1),
            Line("Ana salary", PayPeriods.Monthly, 900m, member: left, order: 1),
            Line("Apartment rent", PayPeriods.Monthly, 250_000m, currency: "CRC", order: 3), // household income, no member
        ];

        var rows = IncomeSnapshot.Rows(lines, Tenant, month, September2026, new HashSet<Guid> { stays }, Now);

        Assert.Equal(["Allan salary", "Freelance", "Apartment rent"], rows.Select(r => r.Label));
        Assert.Equal([0, 1, 2], rows.Select(r => r.SortOrder));
        var allan = rows[0];
        Assert.Equal(2_500m, allan.Amount);
        Assert.Equal(2_500m, allan.PlannedAmount);
        Assert.Equal(("USD", stays, month, Tenant, lines[1].Id), (allan.Currency, allan.MemberUserId!.Value, allan.MonthId, allan.TenantId, allan.IncomeLineId!.Value));
        Assert.Equal((Now, Now), (allan.CreatedAt, allan.UpdatedAt));
        Assert.Equal("CRC", rows[2].Currency);
        Assert.Null(rows[2].MemberUserId);
    }
}
