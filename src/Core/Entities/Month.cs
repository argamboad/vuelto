using Vuelto.Core.Budget;

namespace Vuelto.Core.Entities;

/// <summary>
/// A budget month (ADR-V005): the pay-cycle window that starts at the household's anchor. Exists
/// <b>only</b> through transactions — auto-created when the first transaction lands in an uncovered
/// window, deleted with its weeks when the last one leaves. Weeks and <see cref="WeekCount"/> are
/// computed once from <see cref="BudgetSettings"/> and stored, so a later settings change never
/// re-slices history. Its income is the <see cref="MonthIncome"/> rows snapshotted from the household's
/// <see cref="IncomeLine"/>s at creation (INCOME-1), each editable. Stores no exchange rate (ADR-V006).
/// </summary>
public class Month : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public int Year { get; set; }
    public int MonthNumber { get; set; }
    public int WeekCount { get; set; }
    public DateOnly Week1StartDate { get; set; }
    // ---- Legacy (INCOME-1, ADR-V023): the old two income slots. Kept, untouched, as the rollback baseline for the
    // AddIncomeLines migration; no code reads or writes them (ArchitectureTests guards it). Dropped by INCOME-3.
    public decimal PrimaryIncomeAmount { get; set; }
    public string PrimaryIncomeCurrency { get; set; } = Currencies.Usd;
    public decimal SecondaryIncomeAmount { get; set; }
    public string SecondaryIncomeCurrency { get; set; } = Currencies.Usd;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
