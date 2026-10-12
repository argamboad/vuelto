using Vuelto.Core.Budget;

namespace Vuelto.Core.Entities;

/// <summary>
/// INCOME-1 (ADR-V023): one of the household's incomes — whose it is, its currency, whether it is fixed or an
/// estimate, how often it is paid and how much per payment. A catalog entry (ADR-V008: unique name per household,
/// soft delete, 409 reactivation offer) with a <see cref="SortOrder"/> the reorder endpoint owns. Nothing here is a
/// month figure: each new month snapshots its plan from these lines (<see cref="IncomeSnapshot"/>) into
/// <see cref="MonthIncome"/> rows. Never seeded.
/// </summary>
public class IncomeLine : ICatalogEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public required string Name { get; set; }

    /// <summary>The household member this income belongs to; null for shared income (rent from a property…).</summary>
    public Guid? MemberUserId { get; set; }

    /// <summary>One of <see cref="Currencies"/> — the currency the money arrives in.</summary>
    public string Currency { get; set; } = Currencies.Usd;

    /// <summary>One of <see cref="IncomeKinds"/>.</summary>
    public string Kind { get; set; } = IncomeKinds.Fixed;

    /// <summary>One of <see cref="PayPeriods"/>.</summary>
    public string PayPeriod { get; set; } = PayPeriods.Monthly;

    /// <summary>What one payment brings, in <see cref="Currency"/> (2 dp, positive).</summary>
    public decimal Amount { get; set; }

    /// <summary>Biweekly only: the two days of the month the money lands (1–31; 31 = the month's last day). Null otherwise.</summary>
    public int? PayDay1 { get; set; }
    public int? PayDay2 { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    /// <summary>Set only by the INCOME-1 migration when an old 4w/5w pair didn't fit a pay period; any update clears it.</summary>
    public bool NeedsReview { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
