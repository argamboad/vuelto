using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;
using Vuelto.Infrastructure.Email;

namespace Vuelto.Api.Features.Reports.Pdf;

/// <summary>
/// REPORTS-7: builds the report PDF from exactly what the Reports page reads — <see cref="ReportHandler.AnalyzeAsync"/>,
/// the months trend and the month's pending refunds (single month), and <see cref="ReportHandler.ExportRowsAsync"/> for
/// the appendix — then stores it through <see cref="IFileStorage"/> behind the CSV's 15-minute signed link. Reads only;
/// every query is tenant-filtered through <c>Query()</c>, and the household name comes from the caller's own tenant.
/// REPORTS-8: <see cref="EmailAsync"/> mails the same bytes to the caller as an attachment (the platform's JOBS-4 seam),
/// and both paths speak the language saved in the caller's account unless the request names one.
/// </summary>
public sealed class ReportPdfHandler(
    ReportHandler reports,
    IRepository<Month> months,
    IRepository<Refund> refunds,
    ITenantRepository tenants,
    IUserRepository users,
    ICurrentTenant currentTenant,
    IFileStorage files,
    IEmailSender email,
    TimeProvider clock)
{
    public const string ContentType = "application/pdf";
    private static readonly string[] Languages = ["en", "es"];

    /// <summary>
    /// Validates the "how to show it" half of the request (the period half goes through
    /// <see cref="ReportHandler.ResolvePeriodAsync"/>). The language is the request's when it names one, else the one
    /// saved in <paramref name="userId"/>'s account settings (a language the PDF doesn't ship reads as English).
    /// </summary>
    public async Task<(ReportPdfOptions? Options, ErrorResponse? Error)> ParseOptionsAsync(ReportPdfRequest request, Guid userId, CancellationToken cancellationToken)
    {
        var display = request.Display is null ? DisplayCurrencies.Both : DisplayCurrencies.Normalize(request.Display);
        if (display is null)
            return (null, new ErrorResponse("invalid_request", "display must be CRC, USD or both."));

        var chart = request.ChartCurrency is null ? Currencies.Crc : DisplayCurrencies.Normalize(request.ChartCurrency);
        if (chart is not (Currencies.Crc or Currencies.Usd))
            return (null, new ErrorResponse("invalid_request", "chart_currency must be CRC or USD."));

        string language;
        if (request.Language is { } requested)
        {
            language = requested.Trim().ToLowerInvariant();
            if (!Languages.Contains(language))
                return (null, new ErrorResponse("invalid_request", "language must be en or es."));
        }
        else
        {
            var saved = (await users.GetByIdAsync(userId, cancellationToken))?.Locale?.Trim().ToLowerInvariant();
            language = saved is not null && Languages.Contains(saved) ? saved : "en";
        }

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

    /// <summary>What "Email me" did: the 202 body, or why nothing was sent.</summary>
    public sealed record EmailResult(ReportEmailResponse? Response, ErrorResponse? Error = null, bool UnknownUser = false);

    /// <summary>
    /// Renders the report and queues ONE email, through the outbox, to the caller's own address — there is no
    /// recipient input — with the PDF attached. Nothing is stored: the attachment is the durable copy. A file past the
    /// attachment limit is refused before anything is queued.
    /// </summary>
    public async Task<EmailResult> EmailAsync(ReportPeriod period, ReportPdfOptions options, Guid userId, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(userId, cancellationToken);
        if (user is null || string.IsNullOrWhiteSpace(user.Email)) return new EmailResult(null, UnknownUser: true);

        var report = await RenderAsync(period, options, cancellationToken);
        var (subject, text) = ReportPdfModelBuilder.EmailText(report.Model);
        var body = BrandedEmail.Notification(subject, text, report.Model.Culture);
        try
        {
            await email.SendAsync(user.Email, body.Subject, body.Html, body.InlineImages,
                attachments: [new EmailAttachment(report.FileName, report.Content, ContentType)],
                cancellationToken: cancellationToken);
        }
        catch (ArgumentException)
        {
            return new EmailResult(null, new ErrorResponse("report_too_large",
                "The report is too large to attach. Leave the transactions out and try again."));
        }
        return new EmailResult(new ReportEmailResponse(user.Email, report.FileName, new ReportPeriodResponse(period.From, period.To)));
    }
}
