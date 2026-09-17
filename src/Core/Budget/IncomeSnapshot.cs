using Vuelto.Core.Entities;

namespace Vuelto.Core.Budget;

/// <summary>
/// INCOME-1 (ADR-V023): what each income line contributes to a new budget month. The month is still the unit
/// (ADR-V005); the pay period only says how a line's per-payment amount becomes that month's plan:
/// <list type="bullet">
/// <item><b>weekly</b> — × the month's week count. With the week starting on the payday, weeks = paydays.</item>
/// <item><b>biweekly</b> — × the pay days (two days of the month, 31 = the last day) that fall inside the window from
/// the first week's start to the last week's end.</item>
/// <item><b>monthly</b> — × 1, whatever the week count.</item>
/// </list>
/// Only active lines snapshot, and a line whose member is no longer in the household is skipped — that is computed
/// here, never stored on the line. Pure; amounts 2 dp.
/// </summary>
public static class IncomeSnapshot
{
    /// <summary>How many of the two pay days fall inside [<paramref name="from"/>, <paramref name="to"/>] (inclusive), month by month.</summary>
    public static int PayDaysIn(int payDay1, int payDay2, DateOnly from, DateOnly to)
    {
        if (to < from) return 0;
        var count = 0;
        for (var month = new DateOnly(from.Year, from.Month, 1); month <= to; month = month.AddMonths(1))
        {
            var days = DateTime.DaysInMonth(month.Year, month.Month);
            foreach (var day in new[] { payDay1, payDay2 }.Select(d => Math.Min(d, days)).Distinct())
            {
                var date = new DateOnly(month.Year, month.Month, day);
                if (date >= from && date <= to) count++;
            }
        }
        return count;
    }

    /// <summary>The month's planned amount for one line, given the month's weeks (in order).</summary>
    public static decimal PlannedAmount(IncomeLine line, IReadOnlyList<WeekBoundary> weeks)
    {
        if (weeks.Count == 0) return 0m;
        var times = line.PayPeriod switch
        {
            PayPeriods.Weekly => weeks.Count,
            PayPeriods.Biweekly => PayDaysIn(line.PayDay1 ?? PayPeriods.DefaultFirstPayDay, line.PayDay2 ?? PayPeriods.LastDayOfMonth,
                weeks[0].StartDate, weeks[^1].EndDate),
            _ => 1,
        };
        return CurrencyMath.Round2(line.Amount * times);
    }

    /// <summary>
    /// The rows a new month starts with: one per active line (list order) whose member — if it has one — is still in
    /// the household, each with <see cref="MonthIncome.Amount"/> = <see cref="MonthIncome.PlannedAmount"/>.
    /// </summary>
    public static IReadOnlyList<MonthIncome> Rows(
        IEnumerable<IncomeLine> lines, Guid tenantId, Guid monthId, IReadOnlyList<WeekBoundary> weeks,
        IReadOnlySet<Guid> currentMembers, DateTimeOffset now)
    {
        var order = 0;
        return lines
            .Where(l => l.IsActive && (l.MemberUserId is not { } member || currentMembers.Contains(member)))
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .Select(l =>
            {
                var planned = PlannedAmount(l, weeks);
                return new MonthIncome
                {
                    TenantId = tenantId, MonthId = monthId, IncomeLineId = l.Id, Label = l.Name, MemberUserId = l.MemberUserId,
                    Currency = l.Currency, Amount = planned, PlannedAmount = planned, SortOrder = order++, CreatedAt = now, UpdatedAt = now,
                };
            })
            .ToList();
    }
}
