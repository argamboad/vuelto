using System.Text.Json.Serialization;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Features.Budget;

// BUDGET-1 DTOs. Wire format is snake_case via explicit names (the platform's convention; ADR-V012).

public record UpdateBudgetSettingsRequest(
    [property: JsonPropertyName("week_start_weekday")] int WeekStartWeekday,
    [property: JsonPropertyName("month_anchor")] string? MonthAnchor,
    [property: JsonPropertyName("primary_income_4w")] decimal PrimaryIncome4w,
    [property: JsonPropertyName("primary_income_5w")] decimal PrimaryIncome5w,
    [property: JsonPropertyName("primary_income_currency")] string? PrimaryIncomeCurrency,
    [property: JsonPropertyName("secondary_income_4w")] decimal SecondaryIncome4w,
    [property: JsonPropertyName("secondary_income_5w")] decimal SecondaryIncome5w,
    [property: JsonPropertyName("secondary_income_currency")] string? SecondaryIncomeCurrency);

public record BudgetSettingsResponse(
    [property: JsonPropertyName("week_start_weekday")] int WeekStartWeekday,
    [property: JsonPropertyName("month_anchor")] string MonthAnchor,
    [property: JsonPropertyName("primary_income_4w")] decimal PrimaryIncome4w,
    [property: JsonPropertyName("primary_income_5w")] decimal PrimaryIncome5w,
    [property: JsonPropertyName("primary_income_currency")] string PrimaryIncomeCurrency,
    [property: JsonPropertyName("secondary_income_4w")] decimal SecondaryIncome4w,
    [property: JsonPropertyName("secondary_income_5w")] decimal SecondaryIncome5w,
    [property: JsonPropertyName("secondary_income_currency")] string SecondaryIncomeCurrency,
    [property: JsonPropertyName("is_default")] bool IsDefault,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt)
{
    public static BudgetSettingsResponse From(BudgetSettings s, bool isDefault) => new(
        s.WeekStartWeekday, s.MonthAnchor,
        s.PrimaryIncome4w, s.PrimaryIncome5w, s.PrimaryIncomeCurrency,
        s.SecondaryIncome4w, s.SecondaryIncome5w, s.SecondaryIncomeCurrency,
        isDefault, isDefault ? null : s.UpdatedAt);
}
