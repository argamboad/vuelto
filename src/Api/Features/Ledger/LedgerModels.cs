using System.Text.Json.Serialization;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Features.Ledger;

// LEDGER-1/2 DTOs (ADR-V005/V006/V007). Wire format: snake_case (ADR-V012).

public record WeekResponse(
    [property: JsonPropertyName("week_number")] int WeekNumber,
    [property: JsonPropertyName("start_date")] DateOnly StartDate,
    [property: JsonPropertyName("end_date")] DateOnly EndDate)
{
    public static WeekResponse From(Week w) => new(w.WeekNumber, w.StartDate, w.EndDate);
}

public record MonthResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("month_number")] int MonthNumber,
    [property: JsonPropertyName("week_count")] int WeekCount,
    [property: JsonPropertyName("week1_start_date")] DateOnly Week1StartDate,
    [property: JsonPropertyName("weeks")] IReadOnlyList<WeekResponse>? Weeks,
    [property: JsonPropertyName("income_rows")] IReadOnlyList<MonthIncomeResponse>? IncomeRows = null)
{
    /// <summary>The list carries neither weeks nor income rows; a single month carries both (INCOME-1).</summary>
    public static MonthResponse From(Month m, IReadOnlyList<Week>? weeks = null, IReadOnlyList<MonthIncome>? incomeRows = null) => new(
        m.Id, m.Year, m.MonthNumber, m.WeekCount, m.Week1StartDate,
        weeks?.Select(WeekResponse.From).ToList(),
        incomeRows?.OrderBy(r => r.SortOrder).Select(MonthIncomeResponse.From).ToList());
}

/// <summary>
/// One income row of a month (INCOME-1): <c>planned_amount</c> is what the line's pay period derived when the month was
/// created (null for a one-off, <c>income_line_id</c> null too); <c>amount</c> is what the month counts.
/// </summary>
public record MonthIncomeResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("income_line_id")] Guid? IncomeLineId,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("member_user_id")] Guid? MemberUserId,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("amount")] decimal Amount,
    [property: JsonPropertyName("planned_amount")] decimal? PlannedAmount)
{
    public static MonthIncomeResponse From(MonthIncome r) => new(r.Id, r.IncomeLineId, r.Label, r.MemberUserId, r.Currency, r.Amount, r.PlannedAmount);
}

/// <summary>
/// Which budget month a date belongs to — an uncovered date names the month that WOULD be auto-created (<c>is_new</c>),
/// never a 404. <c>week_number</c> (2026-09-12) is the week of that month's window the date falls in — the stored
/// weeks for an existing month, the boundaries that would create them for a new one — so a form can say
/// "September 2026 · week 3" before anything is saved.
/// </summary>
public record MonthResolveResponse(
    [property: JsonPropertyName("month_id")] Guid? MonthId,
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("month_number")] int MonthNumber,
    [property: JsonPropertyName("is_new")] bool IsNew,
    [property: JsonPropertyName("week_number")] int? WeekNumber = null);

/// <summary>
/// <c>PUT /api/months/{id}/income</c> (INCOME-1): the month's full list of income rows, in order. A row with an
/// <c>id</c> updates that row; a row without one is a one-off for this month; a stored row left out is removed.
/// </summary>
public record UpdateMonthIncomeRequest([property: JsonPropertyName("rows")] List<MonthIncomeRowRequest>? Rows);

public record MonthIncomeRowRequest(
    [property: JsonPropertyName("id")] Guid? Id,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("member_user_id")] Guid? MemberUserId,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("amount")] decimal Amount);

public record CreateTransactionRequest(
    [property: JsonPropertyName("payee")] string? Payee,
    [property: JsonPropertyName("bank_id")] Guid? BankId,
    [property: JsonPropertyName("payment_method")] string? PaymentMethod,
    [property: JsonPropertyName("original_amount")] decimal OriginalAmount,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("transaction_date")] DateOnly? TransactionDate,
    [property: JsonPropertyName("category_id")] Guid? CategoryId,
    [property: JsonPropertyName("transaction_type")] string? TransactionType,
    [property: JsonPropertyName("exchange_rate")] decimal? ExchangeRate,
    [property: JsonPropertyName("envelope_id")] Guid? EnvelopeId,
    [property: JsonPropertyName("refund_expected")] bool RefundExpected = false,
    [property: JsonPropertyName("refund_percentage")] decimal? RefundPercentage = null,
    [property: JsonPropertyName("card_id")] Guid? CardId = null,
    [property: JsonPropertyName("notes")] string? Notes = null,
    // The refund's notes (LEDGER-4), taken at entry (2026-09-14): only mean something with a refund; blank clears.
    [property: JsonPropertyName("refund_notes")] string? RefundNotes = null);

public record UpdateTransactionRequest(
    [property: JsonPropertyName("payee")] string? Payee,
    [property: JsonPropertyName("bank_id")] Guid? BankId,
    [property: JsonPropertyName("payment_method")] string? PaymentMethod,
    [property: JsonPropertyName("original_amount")] decimal OriginalAmount,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("transaction_date")] DateOnly? TransactionDate,
    [property: JsonPropertyName("category_id")] Guid? CategoryId,
    [property: JsonPropertyName("transaction_type")] string? TransactionType,
    [property: JsonPropertyName("envelope_id")] Guid? EnvelopeId,
    [property: JsonPropertyName("refund_expected")] bool RefundExpected = false,
    [property: JsonPropertyName("refund_percentage")] decimal? RefundPercentage = null,
    [property: JsonPropertyName("card_id")] Guid? CardId = null,
    [property: JsonPropertyName("notes")] string? Notes = null,
    // null = leave the refund's notes alone, blank = clear them (the edit form always sends them while a refund is on).
    [property: JsonPropertyName("refund_notes")] string? RefundNotes = null);

public record TransactionResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("month_id")] Guid MonthId,
    [property: JsonPropertyName("payee")] string Payee,
    [property: JsonPropertyName("bank_id")] Guid BankId,
    [property: JsonPropertyName("payment_method")] string PaymentMethod,
    [property: JsonPropertyName("original_amount")] decimal OriginalAmount,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("transaction_date")] DateOnly TransactionDate,
    [property: JsonPropertyName("category_id")] Guid CategoryId,
    [property: JsonPropertyName("amount_crc")] decimal AmountCrc,
    [property: JsonPropertyName("amount_usd")] decimal AmountUsd,
    [property: JsonPropertyName("exchange_rate_used")] decimal ExchangeRateUsed,
    [property: JsonPropertyName("transaction_type")] string TransactionType,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("envelope_id")] Guid? EnvelopeId,
    [property: JsonPropertyName("refund_expected")] bool RefundExpected,
    [property: JsonPropertyName("refund_percentage")] decimal? RefundPercentage,
    [property: JsonPropertyName("card_id")] Guid? CardId = null,
    [property: JsonPropertyName("notes")] string? Notes = null,
    [property: JsonPropertyName("refund_notes")] string? RefundNotes = null)
{
    public static TransactionResponse From(Transaction t, Refund? refund) => new(
        t.Id, t.MonthId, t.Payee, t.BankId, t.PaymentMethod, t.OriginalAmount, t.Currency, t.TransactionDate,
        t.CategoryId, t.AmountCrc, t.AmountUsd, t.ExchangeRateUsed, t.TransactionType, t.Source, t.EnvelopeId,
        refund is not null, refund?.Percentage, t.CardId, t.Notes, refund?.Notes);
}

/// <summary>A month's expected refund (LEDGER-3): derived from its transaction; only <c>status</c> is edited directly.</summary>
public record RefundResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("month_id")] Guid MonthId,
    [property: JsonPropertyName("transaction_id")] Guid TransactionId,
    [property: JsonPropertyName("payee")] string Payee,
    [property: JsonPropertyName("transaction_date")] DateOnly TransactionDate,
    [property: JsonPropertyName("percentage")] decimal Percentage,
    [property: JsonPropertyName("amount_crc")] decimal AmountCrc,
    [property: JsonPropertyName("amount_usd")] decimal AmountUsd,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("inflow_transaction_id")] Guid? InflowTransactionId,
    [property: JsonPropertyName("received_date")] DateOnly? ReceivedDate = null,
    [property: JsonPropertyName("inflow_month_id")] Guid? InflowMonthId = null,
    [property: JsonPropertyName("notes")] string? Notes = null)
{
    /// <param name="inflowMonthId">The month the realized inflow lives in (ADR-V017) — null when pending or unknown to the caller.</param>
    public static RefundResponse From(Refund r, Guid? inflowMonthId = null) =>
        new(r.Id, r.MonthId, r.TransactionId, r.Payee, r.TransactionDate, r.Percentage, r.AmountCrc, r.AmountUsd, r.Status, r.InflowTransactionId, r.ReceivedDate, inflowMonthId, r.Notes);
}

/// <summary><c>received_date</c> (only read for <c>received</c>) dates the inflow and picks its month; unset = today (ADR-V017).</summary>
public record UpdateRefundStatusRequest([property: JsonPropertyName("status")] string? Status, [property: JsonPropertyName("received_date")] DateOnly? ReceivedDate = null);

/// <summary>
/// LEDGER-4: the field the household owns on a refund — why it is expected (case number and all; the separate
/// <c>case_number</c> was folded into it on 2026-09-14). Replaced wholesale by what is sent; blank clears. Kept off
/// the status route on purpose: there, an omitted field would be ambiguous between "leave alone" and "clear".
/// </summary>
public record UpdateRefundDetailsRequest([property: JsonPropertyName("notes")] string? Notes);

/// <summary>A month's transaction row with the catalog names resolved — inactive names still render (ADR-V008).</summary>
public record TransactionListItemResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("payee")] string Payee,
    [property: JsonPropertyName("transaction_date")] DateOnly TransactionDate,
    [property: JsonPropertyName("category_name")] string? CategoryName,
    [property: JsonPropertyName("bank_name")] string? BankName,
    [property: JsonPropertyName("payment_method")] string PaymentMethod,
    [property: JsonPropertyName("transaction_type")] string TransactionType,
    [property: JsonPropertyName("amount_crc")] decimal AmountCrc,
    [property: JsonPropertyName("amount_usd")] decimal AmountUsd,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("card_name")] string? CardName = null,
    [property: JsonPropertyName("notes")] string? Notes = null);
