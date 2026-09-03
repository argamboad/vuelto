using System.Text.Json.Serialization;
using Vuelto.Api.Services;
using Vuelto.Core.Budget;

namespace Vuelto.Api.Features.Expenses;

// EXPENSES-1 DTOs — one shape for fixed and variable lines. Wire format: snake_case (ADR-V012).

public record CreateExpenseRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("budget_crc")] decimal BudgetCrc,
    [property: JsonPropertyName("budget_usd")] decimal BudgetUsd,
    [property: JsonPropertyName("payment_method")] string? PaymentMethod,
    [property: JsonPropertyName("category_id")] Guid? CategoryId,
    [property: JsonPropertyName("bank_id")] Guid? BankId);

public record UpdateExpenseRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("budget_crc")] decimal BudgetCrc,
    [property: JsonPropertyName("budget_usd")] decimal BudgetUsd,
    [property: JsonPropertyName("payment_method")] string? PaymentMethod,
    [property: JsonPropertyName("category_id")] Guid? CategoryId,
    [property: JsonPropertyName("bank_id")] Guid? BankId,
    [property: JsonPropertyName("is_active")] bool IsActive);

public record ReorderExpenseRequest([property: JsonPropertyName("ordered_ids")] List<Guid>? OrderedIds);

public record ExpenseResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("budget_crc")] decimal BudgetCrc,
    [property: JsonPropertyName("budget_usd")] decimal BudgetUsd,
    [property: JsonPropertyName("payment_method")] string PaymentMethod,
    [property: JsonPropertyName("category_id")] Guid CategoryId,
    [property: JsonPropertyName("bank_id")] Guid? BankId,
    [property: JsonPropertyName("sort_order")] int SortOrder,
    [property: JsonPropertyName("is_active")] bool IsActive)
{
    public static ExpenseResponse From(IExpenseLine e) => new(e.Id, e.Name, e.BudgetCrc, e.BudgetUsd, e.PaymentMethod, e.CategoryId, e.BankId, e.SortOrder, e.IsActive);
}

/// <summary>The 409 body for a name clash — same contract as the catalogs: <c>existing_id</c> + <c>existing_name</c> only for an inactive clash.</summary>
public record ExpenseConflictResponse(
    string Error,
    string Message,
    [property: JsonPropertyName("existing_id")] Guid? ExistingId,
    [property: JsonPropertyName("existing_name")] string? ExistingName) : ErrorResponse(Error, Message);
