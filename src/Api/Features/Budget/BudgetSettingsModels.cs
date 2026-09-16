using System.Text.Json.Serialization;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Features.Budget;

// BUDGET-1 DTOs. Wire format is snake_case via explicit names (the platform's convention; ADR-V012).
// INCOME-1 (ADR-V023): the income defaults left the settings; incomes are lines under /api/incomes.

public record UpdateBudgetSettingsRequest(
    [property: JsonPropertyName("week_start_weekday")] int WeekStartWeekday,
    [property: JsonPropertyName("month_anchor")] string? MonthAnchor);

public record BudgetSettingsResponse(
    [property: JsonPropertyName("week_start_weekday")] int WeekStartWeekday,
    [property: JsonPropertyName("month_anchor")] string MonthAnchor,
    [property: JsonPropertyName("is_default")] bool IsDefault,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt)
{
    public static BudgetSettingsResponse From(BudgetSettings s, bool isDefault) => new(
        s.WeekStartWeekday, s.MonthAnchor,
        isDefault, isDefault ? null : s.UpdatedAt);
}
