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
    [property: JsonPropertyName("primary_income_amount")] decimal PrimaryIncomeAmount,
    [property: JsonPropertyName("primary_income_currency")] string PrimaryIncomeCurrency,
    [property: JsonPropertyName("secondary_income_amount")] decimal SecondaryIncomeAmount,
    [property: JsonPropertyName("secondary_income_currency")] string SecondaryIncomeCurrency,
    [property: JsonPropertyName("weeks")] IReadOnlyList<WeekResponse>? Weeks)
{
    public static MonthResponse From(Month m, IReadOnlyList<Week>? weeks = null) => new(
        m.Id, m.Year, m.MonthNumber, m.WeekCount, m.Week1StartDate,
        m.PrimaryIncomeAmount, m.PrimaryIncomeCurrency, m.SecondaryIncomeAmount, m.SecondaryIncomeCurrency,
        weeks?.Select(WeekResponse.From).ToList());
}

/// <summary>Which budget month a date belongs to — an uncovered date names the month that WOULD be auto-created (<c>is_new</c>), never a 404.</summary>
public record MonthResolveResponse(
    [property: JsonPropertyName("month_id")] Guid? MonthId,
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("month_number")] int MonthNumber,
    [property: JsonPropertyName("is_new")] bool IsNew);

public record UpdateMonthIncomeRequest(
    [property: JsonPropertyName("primary_income_amount")] decimal PrimaryIncomeAmount,
    [property: JsonPropertyName("primary_income_currency")] string? PrimaryIncomeCurrency,
    [property: JsonPropertyName("secondary_income_amount")] decimal SecondaryIncomeAmount,
    [property: JsonPropertyName("secondary_income_currency")] string? SecondaryIncomeCurrency);

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
    [property: JsonPropertyName("envelope_id")] Guid? EnvelopeId);

public record UpdateTransactionRequest(
    [property: JsonPropertyName("payee")] string? Payee,
    [property: JsonPropertyName("bank_id")] Guid? BankId,
    [property: JsonPropertyName("payment_method")] string? PaymentMethod,
    [property: JsonPropertyName("original_amount")] decimal OriginalAmount,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("transaction_date")] DateOnly? TransactionDate,
    [property: JsonPropertyName("category_id")] Guid? CategoryId,
    [property: JsonPropertyName("transaction_type")] string? TransactionType,
    [property: JsonPropertyName("envelope_id")] Guid? EnvelopeId);

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
    [property: JsonPropertyName("envelope_id")] Guid? EnvelopeId)
{
    public static TransactionResponse From(Transaction t) => new(
        t.Id, t.MonthId, t.Payee, t.BankId, t.PaymentMethod, t.OriginalAmount, t.Currency, t.TransactionDate,
        t.CategoryId, t.AmountCrc, t.AmountUsd, t.ExchangeRateUsed, t.TransactionType, t.Source, t.EnvelopeId);
}

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
    [property: JsonPropertyName("source")] string Source);
