namespace Vuelto.Core.Entities;

/// <summary>
/// INCOME-1 (ADR-V023): one income of one budget month — snapshotted from an <see cref="IncomeLine"/> when the month
/// is created (<see cref="PlannedAmount"/> = <see cref="Amount"/> then), or added by hand for that month only
/// (<see cref="IncomeLineId"/> and <see cref="PlannedAmount"/> null). The label, member and currency are copied, so a
/// later change to the line never rewrites history. Deleted with its month. The month's income is the sum of its rows
/// (each converted at the day's rate) plus its inflows — <see cref="Budget.IncomeCalculator"/>.
/// </summary>
public class MonthIncome : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid MonthId { get; set; }

    /// <summary>The line this row came from; null for a one-off, or once that line is gone.</summary>
    public Guid? IncomeLineId { get; set; }

    public required string Label { get; set; }
    public Guid? MemberUserId { get; set; }
    public string Currency { get; set; } = Budget.Currencies.Usd;

    /// <summary>The figure the month counts — the plan until someone corrects it.</summary>
    public decimal Amount { get; set; }

    /// <summary>What the line's pay period derived when the month was created; null for a one-off row.</summary>
    public decimal? PlannedAmount { get; set; }

    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
