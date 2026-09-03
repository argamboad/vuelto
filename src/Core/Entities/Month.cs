using Vuelto.Core.Budget;

namespace Vuelto.Core.Entities;

/// <summary>
/// A budget month (ADR-V005): the pay-cycle window that starts at the household's anchor. Exists
/// <b>only</b> through transactions — auto-created when the first transaction lands in an uncovered
/// window, deleted with its weeks when the last one leaves. Weeks and <see cref="WeekCount"/> are
/// computed once from <see cref="BudgetSettings"/> and stored, so a later settings change never
/// re-slices history. Income is snapshotted from the 4-week / 5-week defaults and stays editable.
/// Stores no exchange rate (ADR-V006).
/// </summary>
public class Month : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public int Year { get; set; }
    public int MonthNumber { get; set; }
    public int WeekCount { get; set; }
    public DateOnly Week1StartDate { get; set; }
    public decimal PrimaryIncomeAmount { get; set; }
    public string PrimaryIncomeCurrency { get; set; } = Currencies.Usd;
    public decimal SecondaryIncomeAmount { get; set; }
    public string SecondaryIncomeCurrency { get; set; } = Currencies.Usd;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
