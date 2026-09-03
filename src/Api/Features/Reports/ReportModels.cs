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
    [property: JsonPropertyName("budgeted_usd")] decimal? BudgetedUsd)
{
    public static CategorySpendResponse From(CategorySpendEntry e) => new(e.CategoryId, e.CategoryName, e.TotalCrc, e.TotalUsd, e.BudgetedCrc, e.BudgetedUsd);
}

/// <summary><c>GET /api/reports/category-analysis</c>. Budget columns are present only when <c>single_month</c> is true.</summary>
public record CategoryAnalysisResponse(
    [property: JsonPropertyName("period")] ReportPeriodResponse Period,
    [property: JsonPropertyName("single_month")] bool SingleMonth,
    [property: JsonPropertyName("budgeted")] IReadOnlyList<CategorySpendResponse> Budgeted,
    [property: JsonPropertyName("extraordinary")] IReadOnlyList<CategorySpendResponse> Extraordinary,
    [property: JsonPropertyName("unplanned_essential")] IReadOnlyList<CategorySpendResponse> UnplannedEssential)
{
    public static CategoryAnalysisResponse From(CategoryAnalysis a) => new(
        new ReportPeriodResponse(a.From, a.To), a.SingleMonth,
        a.Budgeted.Select(CategorySpendResponse.From).ToList(),
        a.Extraordinary.Select(CategorySpendResponse.From).ToList(),
        a.UnplannedEssential.Select(CategorySpendResponse.From).ToList());
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
