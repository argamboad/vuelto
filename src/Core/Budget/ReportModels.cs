namespace Vuelto.Core.Budget;

// REPORTS-1/2 (port slice P8): value objects for the category analysis report and the CSV export.

/// <summary>Actual spend of one category in a period (frozen amounts); budget columns only for a single-month period.</summary>
public record CategorySpendEntry(Guid CategoryId, string CategoryName, decimal TotalCrc, decimal TotalUsd, decimal? BudgetedCrc, decimal? BudgetedUsd, int TransactionCount = 0);

/// <summary>Spend of one group (a bank, a payment method) over the period — the three expense classes, frozen amounts.</summary>
public record GroupSpendEntry(string Key, string Label, decimal TotalCrc, decimal TotalUsd);

/// <summary>Spend of one calendar day in the period (days with nothing are absent), ascending.</summary>
public record DaySpendEntry(DateOnly Date, decimal TotalCrc, decimal TotalUsd);

/// <summary>
/// Spend grouped by transaction class then category, inclusive period. Inflow and envelope contributions are excluded — they are not spending.
/// REPORTS-4 adds the same spend by bank, by payment method and by day (for the pace line).
/// </summary>
public record CategoryAnalysis(
    DateOnly From,
    DateOnly To,
    bool SingleMonth,
    IReadOnlyList<CategorySpendEntry> Budgeted,
    IReadOnlyList<CategorySpendEntry> Extraordinary,
    IReadOnlyList<CategorySpendEntry> UnplannedEssential,
    IReadOnlyList<GroupSpendEntry> ByBank,
    IReadOnlyList<GroupSpendEntry> ByMethod,
    IReadOnlyList<DaySpendEntry> ByDay);

/// <summary>One CSV line: names resolved from the all-states catalogs so a deactivated category or bank never blanks history.</summary>
public record TransactionExportRow(
    DateOnly Date,
    string Payee,
    string? CategoryName,
    string TransactionType,
    decimal AmountCrc,
    decimal AmountUsd,
    decimal ExchangeRateUsed,
    string PaymentMethod,
    string? BankName,
    string Source,
    string? CardName = null);
