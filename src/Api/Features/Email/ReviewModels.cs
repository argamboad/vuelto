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
    [property: JsonPropertyName("card_brand")] string? CardBrand,
    [property: JsonPropertyName("authorization")] string? Authorization,
    [property: JsonPropertyName("reference")] string? Reference,
    [property: JsonPropertyName("transaction_type")] string? TransactionType,
    [property: JsonPropertyName("missing_fields")] string[] MissingFields,
    [property: JsonPropertyName("suggested_category_id")] Guid? SuggestedCategoryId,
    [property: JsonPropertyName("suggested_class")] string? SuggestedClass,
    [property: JsonPropertyName("received_at")] DateTimeOffset? ReceivedAt,
    // #210 (ADR-V027): what the queue shows for the card ("VISA ····1234", or the printed pattern when its digits are not
    // the last four), whether the household must say which card it is, and the card it said last time for that pattern.
    [property: JsonPropertyName("card_label")] string? CardLabel = null,
    [property: JsonPropertyName("card_ambiguous")] bool CardAmbiguous = false,
    [property: JsonPropertyName("known_card_id")] Guid? KnownCardId = null)
{
    public static PendingVoucherResponse From(PendingVoucher v, IReadOnlyDictionary<string, Guid>? knownPatterns = null)
    {
        var read = Vuelto.Core.Budget.CardIdentity.Read(v.CardNumber);
        Guid? known = read.Pattern is { } pattern && knownPatterns?.TryGetValue(pattern, out var cardId) == true ? cardId : null;
        return new(
            v.Id, v.ParsedBank, v.Merchant, v.Amount, v.Currency, v.Date, v.BankId, v.CardNumber, v.CardBrand, v.Authorization, v.Reference,
            v.TransactionType, v.MissingFields, v.SuggestedCategoryId, v.SuggestedClass, v.ReceivedAt,
            Vuelto.Core.Budget.CardIdentity.Label(v.CardBrand, v.CardNumber), read.Ambiguous, known);
    }
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
    // LEDGER-3 on the queue: same rules as manual entry — an unplanned or discretionary class (ADR-V025), and an amount
    // in the booked currency, 0 < amount ≤ the purchase (ADR-V026).
    [property: JsonPropertyName("refund_expected")] bool RefundExpected = false,
    [property: JsonPropertyName("refund_amount")] decimal? RefundAmount = null,
    // The reason, recorded while the voucher is in front of you — the ledger's optional 250-character note.
    [property: JsonPropertyName("notes")] string? Notes = null,
    // The refund's notes (LEDGER-4), same as the manual form (2026-09-14).
    [property: JsonPropertyName("refund_notes")] string? RefundNotes = null,
    // #210 (ADR-V027): the household's own answer to "which card was it?" — an active card of theirs (remembered for the
    // voucher's pattern when its digits aren't the last four), or skip_card for no card at all. Absent: the card the
    // voucher's text names (CARDS-1), or none for a pattern nobody has mapped yet — never a guess.
    [property: JsonPropertyName("card_id")] Guid? CardId = null,
    [property: JsonPropertyName("skip_card")] bool SkipCard = false);

/// <summary>EMAIL-7: the guard on the queue reset — the client has to say it means it (the household-dissolve shape, ADR-V009).</summary>
public record ClearQueueRequest([property: JsonPropertyName("confirm")] bool Confirm = false);

/// <summary>What the reset did: drafts deleted, and how many inboxes had their cursor pulled back so those emails are read again.</summary>
public record ClearQueueResponse(
    [property: JsonPropertyName("cleared")] int Cleared,
    [property: JsonPropertyName("inboxes_rewound")] int InboxesRewound);

public record ConfirmVoucherResponse(
    [property: JsonPropertyName("transaction_id")] Guid TransactionId,
    [property: JsonPropertyName("month_id")] Guid MonthId,
    [property: JsonPropertyName("amount_crc")] decimal AmountCrc,
    [property: JsonPropertyName("amount_usd")] decimal AmountUsd,
    [property: JsonPropertyName("remembered")] bool Remembered);
