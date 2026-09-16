using System.Text.Json.Serialization;
using Vuelto.Core.Budget;

namespace Vuelto.Api.Features.Reports;

// REPORTS-1/2 wire shapes (snake_case, ADR-V012).

/// <summary>The resolved inclusive period: a month's anchor window (first week start → last week end) or the caller's range.</summary>
public sealed record ReportPeriod(DateOnly From, DateOnly To, Guid? MonthId)
{
    public bool SingleMonth => MonthId is not null;
}

public record ReportPeriodResponse([property: JsonPropertyName("from")] DateOnly From, [property: JsonPropertyName("to")] DateOnly To);

public record CategorySpendResponse(
    [property: JsonPropertyName("category_id")] Guid CategoryId,
    [property: JsonPropertyName("category_name")] string CategoryName,
    [property: JsonPropertyName("total_crc")] decimal TotalCrc,
    [property: JsonPropertyName("total_usd")] decimal TotalUsd,
    [property: JsonPropertyName("budgeted_crc")] decimal? BudgetedCrc,
    [property: JsonPropertyName("budgeted_usd")] decimal? BudgetedUsd,
    [property: JsonPropertyName("transaction_count")] int TransactionCount)
{
    public static CategorySpendResponse From(CategorySpendEntry e) => new(e.CategoryId, e.CategoryName, e.TotalCrc, e.TotalUsd, e.BudgetedCrc, e.BudgetedUsd, e.TransactionCount);
}

/// <summary>A dual-currency amount on the report wire (own record — slices never share DTOs, R7).</summary>
public record ReportMoneyResponse([property: JsonPropertyName("crc")] decimal Crc, [property: JsonPropertyName("usd")] decimal Usd)
{
    public static ReportMoneyResponse From(MoneyPair p) => new(p.Crc, p.Usd);
}

/// <summary>Spend of one bank or payment method over the period (REPORTS-4). <c>key</c> is the bank id or the method code.</summary>
public record GroupSpendResponse(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("total_crc")] decimal TotalCrc,
    [property: JsonPropertyName("total_usd")] decimal TotalUsd)
{
    public static GroupSpendResponse From(GroupSpendEntry e) => new(e.Key, e.Label, e.TotalCrc, e.TotalUsd);
}

/// <summary>Spend of one day (REPORTS-4, the pace line); days without spend are absent.</summary>
public record DaySpendResponse(
    [property: JsonPropertyName("date")] DateOnly Date,
    [property: JsonPropertyName("total_crc")] decimal TotalCrc,
    [property: JsonPropertyName("total_usd")] decimal TotalUsd)
{
    public static DaySpendResponse From(DaySpendEntry e) => new(e.Date, e.TotalCrc, e.TotalUsd);
}

/// <summary>One month of <c>GET /api/reports/months-trend</c> (REPORTS-4). <c>income</c> is null when no rate could be resolved.</summary>
public record MonthTrendResponse(
    [property: JsonPropertyName("month_id")] Guid MonthId,
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("month_number")] int MonthNumber,
    [property: JsonPropertyName("income")] ReportMoneyResponse? Income,
    [property: JsonPropertyName("spend")] ReportMoneyResponse Spend)
{
    public static MonthTrendResponse From(MonthTrendEntry e) => new(e.MonthId, e.Year, e.MonthNumber,
        e.Income is null ? null : ReportMoneyResponse.From(e.Income), ReportMoneyResponse.From(e.Spend));
}

/// <summary><c>GET /api/reports/months-trend?count=</c>: the household's last <c>count</c> months, oldest first.</summary>
public record MonthsTrendResponse(
    [property: JsonPropertyName("months")] IReadOnlyList<MonthTrendResponse> Months,
    [property: JsonPropertyName("rate_available")] bool RateAvailable);

/// <summary>
/// <c>GET /api/reports/category-analysis</c>. Budget columns are present only when <c>single_month</c> is true.
/// <c>income</c> (REPORTS-3) is the month's income — configured incomes at today's rate plus inflows, the
/// dashboard's own definition — and is <c>null</c> for a date range (income is per month) or when no rate
/// can be resolved (ADR-V006 chain exhausted); spend totals never depend on it. <c>budget_total</c> is the
/// sum of every active budget line converted at the same rate (each line is single-currency), null in the
/// same cases.
/// </summary>
public record CategoryAnalysisResponse(
    [property: JsonPropertyName("period")] ReportPeriodResponse Period,
    [property: JsonPropertyName("single_month")] bool SingleMonth,
    [property: JsonPropertyName("budgeted")] IReadOnlyList<CategorySpendResponse> Budgeted,
    [property: JsonPropertyName("extraordinary")] IReadOnlyList<CategorySpendResponse> Extraordinary,
    [property: JsonPropertyName("unplanned_essential")] IReadOnlyList<CategorySpendResponse> UnplannedEssential,
    [property: JsonPropertyName("income")] ReportMoneyResponse? Income,
    [property: JsonPropertyName("budget_total")] ReportMoneyResponse? BudgetTotal,
    [property: JsonPropertyName("exchange_rate")] decimal? ExchangeRate,
    [property: JsonPropertyName("exchange_rate_buy")] decimal? ExchangeRateBuy,
    [property: JsonPropertyName("budget_by_method")] IReadOnlyList<GroupSpendResponse>? BudgetByMethod,
    [property: JsonPropertyName("by_bank")] IReadOnlyList<GroupSpendResponse> ByBank,
    [property: JsonPropertyName("by_method")] IReadOnlyList<GroupSpendResponse> ByMethod,
    [property: JsonPropertyName("spend_by_day")] IReadOnlyList<DaySpendResponse>? SpendByDay,
    [property: JsonPropertyName("by_card")] IReadOnlyList<GroupSpendResponse> ByCard)
{
    /// <summary><c>spend_by_day</c> only for a single month (the pace line is a month picture; a long range would ship hundreds of rows for nothing).</summary>
    public static CategoryAnalysisResponse From(CategoryAnalysis a, MoneyPair? income, MoneyPair? budgetTotal, FxRates? rates = null, IReadOnlyList<GroupSpendEntry>? budgetByMethod = null) => new(
        new ReportPeriodResponse(a.From, a.To), a.SingleMonth,
        a.Budgeted.Select(CategorySpendResponse.From).ToList(),
        a.Extraordinary.Select(CategorySpendResponse.From).ToList(),
        a.UnplannedEssential.Select(CategorySpendResponse.From).ToList(),
        income is null ? null : ReportMoneyResponse.From(income),
        budgetTotal is null ? null : ReportMoneyResponse.From(budgetTotal),
        rates?.Sell, rates?.Buy, // the pair the projections used (ADR-V019) — so a client can total single-currency budgets in one currency
        budgetByMethod?.Select(GroupSpendResponse.From).ToList(), // the plan cut by payment method (single month with a rate), beside by_method's spend
        a.ByBank.Select(GroupSpendResponse.From).ToList(),
        a.ByMethod.Select(GroupSpendResponse.From).ToList(),
        a.SingleMonth ? a.ByDay.Select(DaySpendResponse.From).ToList() : null,
        a.ByCard.Select(c => new GroupSpendResponse(c.CardId?.ToString() ?? "none", c.CardName, c.TotalCrc, c.TotalUsd)).ToList()); // CARDS-2: key "none" = no card
}

/// <summary>
/// <c>POST /api/reports/transactions/export</c>: the CSV is stored through <c>IFileStorage</c> and a
/// signed, time-limited link is returned (ADR-010) — the same download affordance the household export
/// uses, so the shared <c>IFileDownloadLauncher</c> works in a browser and in the MAUI shells alike.
/// </summary>
public record TransactionExportResponse(
    [property: JsonPropertyName("download_url")] string DownloadUrl,
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("row_count")] int RowCount,
    [property: JsonPropertyName("period")] ReportPeriodResponse Period,
    [property: JsonPropertyName("expires_in_seconds")] int ExpiresInSeconds);

/// <summary>
/// <c>POST /api/reports/pdf</c> (REPORTS-7): the period (<c>month_id</c> or <c>from</c>+<c>to</c>, the shared rule) and how
/// to show it — <c>display</c> CRC | USD | both (default both), <c>chart_currency</c> CRC | USD (default CRC),
/// <c>include_appendix</c> (default true), <c>language</c> en | es (default en), and <c>today</c>, the device's date for
/// the pace marker (default the server's UTC date).
/// </summary>
public record ReportPdfRequest(
    [property: JsonPropertyName("month_id")] Guid? MonthId = null,
    [property: JsonPropertyName("from")] string? From = null,
    [property: JsonPropertyName("to")] string? To = null,
    [property: JsonPropertyName("display")] string? Display = null,
    [property: JsonPropertyName("chart_currency")] string? ChartCurrency = null,
    [property: JsonPropertyName("include_appendix")] bool? IncludeAppendix = null,
    [property: JsonPropertyName("language")] string? Language = null,
    [property: JsonPropertyName("today")] DateOnly? Today = null);

/// <summary>The stored PDF behind a signed, time-limited link — the CSV export's delivery (ADR-010), same launcher on web and native.</summary>
public record ReportPdfResponse(
    [property: JsonPropertyName("download_url")] string DownloadUrl,
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("period")] ReportPeriodResponse Period,
    [property: JsonPropertyName("expires_in_seconds")] int ExpiresInSeconds);
