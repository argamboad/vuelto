using System.Text.Json.Serialization;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Features.Email;

// EMAIL-5/6 DTOs (ADR-V010): merchant → category rules and the review queue. Wire format: snake_case.

public record CreateMerchantMappingRequest(
    [property: JsonPropertyName("merchant_pattern")] string? MerchantPattern,
    [property: JsonPropertyName("category_id")] Guid? CategoryId,
    [property: JsonPropertyName("suggested_class")] string? SuggestedClass);

public record UpdateMerchantMappingRequest(
    [property: JsonPropertyName("merchant_pattern")] string? MerchantPattern,
    [property: JsonPropertyName("category_id")] Guid? CategoryId,
    [property: JsonPropertyName("suggested_class")] string? SuggestedClass);

public record MerchantMappingResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("merchant_pattern")] string MerchantPattern,
    [property: JsonPropertyName("category_id")] Guid CategoryId,
    [property: JsonPropertyName("category_name")] string? CategoryName,
    [property: JsonPropertyName("suggested_class")] string? SuggestedClass)
{
    public static MerchantMappingResponse From(MerchantCategoryMapping m, string? categoryName) => new(m.Id, m.MerchantPattern, m.CategoryId, categoryName, m.SuggestedClass);
}

/// <summary>A staged draft as the review queue sees it — the parsed fields, the resolved bank, the suggestion, and what the parser could not read.</summary>
public record PendingVoucherResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("parsed_bank")] string ParsedBank,
    [property: JsonPropertyName("merchant")] string? Merchant,
    [property: JsonPropertyName("amount")] decimal? Amount,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("date")] DateOnly? Date,
    [property: JsonPropertyName("bank_id")] Guid? BankId,
    [property: JsonPropertyName("card_number")] string? CardNumber,
    [property: JsonPropertyName("authorization")] string? Authorization,
    [property: JsonPropertyName("reference")] string? Reference,
    [property: JsonPropertyName("transaction_type")] string? TransactionType,
    [property: JsonPropertyName("missing_fields")] string[] MissingFields,
    [property: JsonPropertyName("suggested_category_id")] Guid? SuggestedCategoryId,
    [property: JsonPropertyName("suggested_class")] string? SuggestedClass,
    [property: JsonPropertyName("received_at")] DateTimeOffset? ReceivedAt)
{
    public static PendingVoucherResponse From(PendingVoucher v) => new(
        v.Id, v.ParsedBank, v.Merchant, v.Amount, v.Currency, v.Date, v.BankId, v.CardNumber, v.Authorization, v.Reference,
        v.TransactionType, v.MissingFields, v.SuggestedCategoryId, v.SuggestedClass, v.ReceivedAt);
}

public record PendingCountResponse([property: JsonPropertyName("count")] int Count);

/// <summary>
/// Confirm a draft: the category and class are the user's decision; the rest defaults to the parsed voucher
/// and may be overridden (the UI opens a field only when the parser left it blank). <c>remember_merchant</c>
/// creates a merchant rule from this confirmation (never overwrites an existing one).
/// </summary>
public record ConfirmVoucherRequest(
    [property: JsonPropertyName("category_id")] Guid? CategoryId,
    [property: JsonPropertyName("transaction_class")] string? TransactionClass,
    [property: JsonPropertyName("payee")] string? Payee = null,
    [property: JsonPropertyName("bank_id")] Guid? BankId = null,
    [property: JsonPropertyName("payment_method")] string? PaymentMethod = null,
    [property: JsonPropertyName("original_amount")] decimal? OriginalAmount = null,
    [property: JsonPropertyName("currency")] string? Currency = null,
    [property: JsonPropertyName("transaction_date")] DateOnly? TransactionDate = null,
    [property: JsonPropertyName("remember_merchant")] bool RememberMerchant = false,
    // LEDGER-3 on the queue: same rules as manual entry — only means something on an unplanned essential, needs 0 < p ≤ 100.
    [property: JsonPropertyName("refund_expected")] bool RefundExpected = false,
    [property: JsonPropertyName("refund_percentage")] decimal? RefundPercentage = null);

public record ConfirmVoucherResponse(
    [property: JsonPropertyName("transaction_id")] Guid TransactionId,
    [property: JsonPropertyName("month_id")] Guid MonthId,
    [property: JsonPropertyName("amount_crc")] decimal AmountCrc,
    [property: JsonPropertyName("amount_usd")] decimal AmountUsd,
    [property: JsonPropertyName("remembered")] bool Remembered);
