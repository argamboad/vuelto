using Vuelto.Core.Budget;

namespace Vuelto.Core.Entities;

/// <summary>
/// A household's budget structure (ADR-V003): the weekday its weeks start on — for a weekly-paid household, the day
/// the money moves — and where its budget month begins. Exactly one row per tenant, created on the first save; before
/// that, <see cref="Defaults"/> is what the app runs on. Income moved to <see cref="IncomeLine"/> (INCOME-1, ADR-V023).
/// </summary>
public class BudgetSettings : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }

    /// <summary>0 = Sunday … 6 = Saturday. Default Thursday.</summary>
    public int WeekStartWeekday { get; set; } = DefaultWeekStartWeekday;

    /// <summary>One of <see cref="MonthAnchors"/>.</summary>
    public string MonthAnchor { get; set; } = MonthAnchors.LastWeekdayPrev;

    // ---- Legacy (INCOME-1, ADR-V023): the old two-income 4w/5w defaults. Kept, untouched, as the rollback baseline
    // for the AddIncomeLines migration; no code reads or writes them (ArchitectureTests guards it). Dropped by INCOME-3.
    public decimal PrimaryIncome4w { get; set; }
    public decimal PrimaryIncome5w { get; set; }
    public string PrimaryIncomeCurrency { get; set; } = Currencies.Usd;
    public decimal SecondaryIncome4w { get; set; }
    public decimal SecondaryIncome5w { get; set; }
    public string SecondaryIncomeCurrency { get; set; } = Currencies.Usd;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public const int DefaultWeekStartWeekday = 4;

    /// <summary>The settings a household runs on until it saves its own — never persisted by a read.</summary>
    public static BudgetSettings Defaults(Guid tenantId) => new() { TenantId = tenantId };
}
