namespace Vuelto.Core.Budget;

// REPORTS-1/2 (port slice P8): value objects for the category analysis report and the CSV export.

/// <summary>Actual spend of one category in a period (frozen amounts); budget columns only for a single-month period.</summary>
public record CategorySpendEntry(Guid CategoryId, string CategoryName, decimal TotalCrc, decimal TotalUsd, decimal? BudgetedCrc, decimal? BudgetedUsd);

/// <summary>Spend grouped by transaction class then category, inclusive period. Inflow and envelope contributions are excluded — they are not spending.</summary>
public record CategoryAnalysis(
    DateOnly From,
    DateOnly To,
    bool SingleMonth,
    IReadOnlyList<CategorySpendEntry> Budgeted,
    IReadOnlyList<CategorySpendEntry> Extraordinary,
    IReadOnlyList<CategorySpendEntry> UnplannedEssential);

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
    string Source);
