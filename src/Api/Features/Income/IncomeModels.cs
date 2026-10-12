using System.Text.Json.Serialization;
using Vuelto.Api.Services;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Features.Income;

// INCOME-1 DTOs (ADR-V023). Wire format: snake_case (ADR-V012).

/// <summary>
/// Create or update a line. <c>pay_days</c> only for a biweekly line (two different days 1–31, 31 = the month's last
/// day; default [15, 31]); <c>is_active</c> is ignored on create.
/// </summary>
public record IncomeLineRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("member_user_id")] Guid? MemberUserId,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("pay_period")] string? PayPeriod,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("pay_days")] List<int>? PayDays = null,
    [property: JsonPropertyName("is_active")] bool IsActive = true);

public record ReorderIncomeRequest([property: JsonPropertyName("ordered_ids")] List<Guid>? OrderedIds);

public record IncomeLineResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("member_user_id")] Guid? MemberUserId,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("pay_period")] string PayPeriod,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("pay_days")] IReadOnlyList<int>? PayDays,
    [property: JsonPropertyName("sort_order")] int SortOrder,
    [property: JsonPropertyName("is_active")] bool IsActive,
    [property: JsonPropertyName("needs_review")] bool NeedsReview)
{
    public static IncomeLineResponse From(IncomeLine l) => new(
        l.Id, l.Name, l.MemberUserId, l.Currency, l.Kind, l.PayPeriod, l.Amount,
        l.PayDay1 is { } d1 && l.PayDay2 is { } d2 ? [d1, d2] : null,
        l.SortOrder, l.IsActive, l.NeedsReview);
}

/// <summary>The 409 body for a name clash — the catalogs' contract: <c>existing_id</c> + <c>existing_name</c> only for an inactive clash.</summary>
public record IncomeConflictResponse(
    string Error,
    string Message,
    [property: JsonPropertyName("existing_id")] Guid? ExistingId,
    [property: JsonPropertyName("existing_name")] string? ExistingName) : ErrorResponse(Error, Message);
