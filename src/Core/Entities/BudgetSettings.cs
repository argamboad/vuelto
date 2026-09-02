using Vuelto.Core.Budget;

namespace Vuelto.Core.Entities;

/// <summary>
/// A household's budget structure (ADR-V003): the weekday its weeks start on, where its budget
/// month begins, and the two incomes' 4-week / 5-week defaults that are snapshotted onto every
/// auto-created month (ADR-V005). Exactly one row per tenant, created on the first save; before
/// that, <see cref="Defaults"/> is what the app runs on.
/// </summary>
public class BudgetSettings : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }

    /// <summary>0 = Sunday … 6 = Saturday. Default Thursday.</summary>
    public int WeekStartWeekday { get; set; } = DefaultWeekStartWeekday;

    /// <summary>One of <see cref="MonthAnchors"/>.</summary>
    public string MonthAnchor { get; set; } = MonthAnchors.LastWeekdayPrev;

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
