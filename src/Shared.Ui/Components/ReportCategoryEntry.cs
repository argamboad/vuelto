using System.Text.Json.Serialization;

namespace Vuelto.Shared.Ui.Components;

/// <summary>One category row of the category-analysis report (REPORTS-1). Budgets are single-currency: at most one side is set.</summary>
public sealed record ReportCategoryEntry(
    [property: JsonPropertyName("category_id")] Guid CategoryId,
    [property: JsonPropertyName("category_name")] string CategoryName,
    [property: JsonPropertyName("total_crc")] decimal TotalCrc,
    [property: JsonPropertyName("total_usd")] decimal TotalUsd,
    [property: JsonPropertyName("budgeted_crc")] decimal? BudgetedCrc,
    [property: JsonPropertyName("budgeted_usd")] decimal? BudgetedUsd,
    [property: JsonPropertyName("transaction_count")] int TransactionCount = 0)
{
    /// <summary>Spend over budget in the line's OWN currency, or null when the line has no budget. Over 1 means over budget.</summary>
    public decimal? Fill => this switch
    {
        { BudgetedCrc: > 0 } => TotalCrc / BudgetedCrc.Value,
        { BudgetedUsd: > 0 } => TotalUsd / BudgetedUsd.Value,
        _ => null,
    };
}
