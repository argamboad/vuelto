using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Reports.Pdf;

/// <summary>
/// REPORTS-7: builds the report PDF from exactly what the Reports page reads — <see cref="ReportHandler.AnalyzeAsync"/>,
/// the months trend and the month's pending refunds (single month), and <see cref="ReportHandler.ExportRowsAsync"/> for
/// the appendix — then stores it through <see cref="IFileStorage"/> behind the CSV's 15-minute signed link. Reads only;
/// every query is tenant-filtered through <c>Query()</c>, and the household name comes from the caller's own tenant.
/// </summary>
public sealed class ReportPdfHandler(
    ReportHandler reports,
    IRepository<Month> months,
    IRepository<Refund> refunds,
    ITenantRepository tenants,
    ICurrentTenant currentTenant,
    IFileStorage files,
    TimeProvider clock)
{
    public const string ContentType = "application/pdf";
    private static readonly string[] Languages = ["en", "es"];

    /// <summary>Validates the "how to show it" half of the request; the period half goes through <see cref="ReportHandler.ResolvePeriodAsync"/>.</summary>
    public (ReportPdfOptions? Options, ErrorResponse? Error) ParseOptions(ReportPdfRequest request)
    {
        var display = request.Display is null ? DisplayCurrencies.Both : DisplayCurrencies.Normalize(request.Display);
        if (display is null)
            return (null, new ErrorResponse("invalid_request", "display must be CRC, USD or both."));

        var chart = request.ChartCurrency is null ? Currencies.Crc : DisplayCurrencies.Normalize(request.ChartCurrency);
        if (chart is not (Currencies.Crc or Currencies.Usd))
            return (null, new ErrorResponse("invalid_request", "chart_currency must be CRC or USD."));

        var language = request.Language?.Trim().ToLowerInvariant() ?? "en";
        if (!Languages.Contains(language))
            return (null, new ErrorResponse("invalid_request", "language must be en or es."));

        var today = request.Today ?? DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        return (new ReportPdfOptions(display, chart, request.IncludeAppendix ?? true, language, today), null);
    }

    /// <summary>The rendered file and its download name — REPORTS-8 mails the same bytes this stores.</summary>
    public sealed record RenderedReport(byte[] Content, string FileName, ReportPdfModel Model);

    public async Task<RenderedReport> RenderAsync(ReportPeriod period, ReportPdfOptions options, CancellationToken cancellationToken)
    {
        var analysis = await reports.AnalyzeAsync(period, cancellationToken);

        ReportPdfMonth? month = null;
        MonthsTrendResponse? trend = null;
        MoneyPair? pending = null;
        if (period.MonthId is { } monthId)
        {
            month = await months.Query().Where(m => m.Id == monthId)
                .Select(m => new ReportPdfMonth(m.Year, m.MonthNumber)).FirstOrDefaultAsync(cancellationToken);
            trend = await reports.TrendAsync(ReportHandler.TrendDefaultCount, cancellationToken);
            // The unplanned tile's "refundable" figure — the dashboard's refunds total: pending only (a received refund is already income).
            var open = await refunds.Query().Where(r => r.MonthId == monthId && r.Status != RefundStatuses.Received)
                .Select(r => new { r.AmountCrc, r.AmountUsd }).ToListAsync(cancellationToken);
            pending = new MoneyPair(CurrencyMath.Round2(open.Sum(r => r.AmountCrc)), CurrencyMath.Round2(open.Sum(r => r.AmountUsd)));
        }

        var appendix = options.IncludeAppendix ? await reports.ExportRowsAsync(period, null, null, cancellationToken) : null;
        var household = currentTenant.TenantId is { } tenantId ? (await tenants.GetByIdAsync(tenantId, cancellationToken))?.Name : null;

        var model = ReportPdfModelBuilder.Build(new ReportPdfInput(
            household ?? "", clock.GetUtcNow(), analysis, month, trend, pending, appendix, options));
        var content = ReportPdfRenderer.Render(model);
        return new RenderedReport(content, $"report-{period.From:yyyy-MM-dd}_{period.To:yyyy-MM-dd}.pdf", model);
    }

    public async Task<ReportPdfResponse> CreateAsync(ReportPeriod period, ReportPdfOptions options, CancellationToken cancellationToken)
    {
        var report = await RenderAsync(period, options, cancellationToken);

        // Same storage shape as the CSV: the basename is the download name; a per-file folder keeps two members apart.
        var now = clock.GetUtcNow();
        var key = $"exports/reports/{now:yyyyMMddTHHmmssZ}-{Guid.CreateVersion7():N}/{report.FileName}";
        using (var stream = new MemoryStream(report.Content))
            await files.PutAsync(key, stream, ContentType, cancellationToken);
        var url = await files.GetDownloadUrlAsync(key, ReportHandler.LinkLifetime, cancellationToken);

        return new ReportPdfResponse(url.ToString(), report.FileName, new ReportPeriodResponse(period.From, period.To), (int)ReportHandler.LinkLifetime.TotalSeconds);
    }
}
