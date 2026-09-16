using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Vuelto.Api.Authentication;
using Vuelto.Api.Endpoints;
using Vuelto.Api.Features.Reports.Pdf;
using Vuelto.Api.Services;

namespace Vuelto.Api.Features.Reports;

/// <summary>
/// REPORTS-1/2/7/8 routes under <c>/api/reports</c> (any household member; tenant-API policy via the group
/// helper). Period selection is shared: <c>month_id</c> <b>or</b> <c>from</c>+<c>to</c> (yyyy-MM-dd,
/// inclusive) — 400 <c>period_required</c> / <c>period_ambiguous</c> / <c>period_incomplete</c> /
/// <c>period_invalid</c>; an unknown or foreign <c>month_id</c> is a uniform 404.
/// </summary>
public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReports(this IEndpointRouteBuilder app)
    {
        var group = app.MapTenantFeatureGroup("/api/reports");

        // GET /api/reports/category-analysis?month_id= | ?from=&to=
        group.MapGet("/category-analysis", async (
            [FromQuery(Name = "month_id")] Guid? monthId, [FromQuery(Name = "from")] string? from, [FromQuery(Name = "to")] string? to,
            ReportHandler handler, CancellationToken ct) =>
        {
            var resolution = await handler.ResolvePeriodAsync(monthId, from, to, ct);
            if (resolution.NotFound) return Results.NotFound(new ErrorResponse("not_found", "month not found"));
            if (resolution.Error is { } error) return Results.BadRequest(error);
            return Results.Ok(await handler.AnalyzeAsync(resolution.Period!, ct));
        });

        // GET /api/reports/months-trend?count=12 — the last N months, oldest first: income (today's rate) vs spend (REPORTS-4)
        group.MapGet("/months-trend", async ([FromQuery(Name = "count")] int? count, ReportHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.TrendAsync(count ?? ReportHandler.TrendDefaultCount, ct)));

        // POST /api/reports/transactions/export?month_id= | ?from=&to=  [&category_id=&class=]
        // A POST because it creates a stored artifact and mints a signed link (ADR-010), like the household export.
        group.MapPost("/transactions/export", async (
            [FromQuery(Name = "month_id")] Guid? monthId, [FromQuery(Name = "from")] string? from, [FromQuery(Name = "to")] string? to,
            [FromQuery(Name = "category_id")] Guid? categoryId, [FromQuery(Name = "class")] string? transactionType,
            ReportHandler handler, CancellationToken ct) =>
        {
            var resolution = await handler.ResolvePeriodAsync(monthId, from, to, ct);
            if (resolution.NotFound) return Results.NotFound(new ErrorResponse("not_found", "month not found"));
            if (resolution.Error is { } error) return Results.BadRequest(error);
            return Results.Ok(await handler.ExportAsync(resolution.Period!, categoryId, transactionType, ct));
        });

        // POST /api/reports/pdf { month_id | from+to, display, chart_currency, include_appendix, language, today } (REPORTS-7)
        // A POST for the same reason as the export: it renders and stores a file, then mints a signed link.
        group.MapPost("/pdf", async (
            [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ReportPdfRequest? body,
            ClaimsPrincipal user, ReportHandler handler, ReportPdfHandler pdf, CancellationToken ct) =>
        {
            if (user.GetUserId() is not { } userId) return Results.Unauthorized();
            var (period, options, failure) = await ResolvePdfAsync(body, userId, handler, pdf, ct);
            if (failure is not null) return failure;
            return Results.Ok(await pdf.CreateAsync(period!, options!, ct));
        });

        // POST /api/reports/pdf/email — same body; one email with the PDF attached to the CALLER's own address (REPORTS-8).
        // 202 { sent_to, file_name, period }; 400 report_too_large; 429 past the daily cap (ReportEmailRateLimit).
        group.MapPost("/pdf/email", async (
            [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] ReportPdfRequest? body,
            ClaimsPrincipal user, ReportHandler handler, ReportPdfHandler pdf, CancellationToken ct) =>
        {
            if (user.GetUserId() is not { } userId) return Results.Unauthorized();
            var (period, options, failure) = await ResolvePdfAsync(body, userId, handler, pdf, ct);
            if (failure is not null) return failure;
            var result = await pdf.EmailAsync(period!, options!, userId, ct);
            if (result.UnknownUser) return Results.Unauthorized();
            if (result.Error is { } error) return Results.BadRequest(error);
            return Results.Accepted(value: result.Response);
        }).RequireRateLimiting(ReportEmailRateLimit.Policy);

        return app;
    }

    /// <summary>The PDF endpoints' shared front half: the period (shared rule, uniform 404) then the display options.</summary>
    private static async Task<(ReportPeriod? Period, ReportPdfOptions? Options, IResult? Failure)> ResolvePdfAsync(
        ReportPdfRequest? body, Guid userId, ReportHandler handler, ReportPdfHandler pdf, CancellationToken ct)
    {
        var request = body ?? new ReportPdfRequest();
        var resolution = await handler.ResolvePeriodAsync(request.MonthId, request.From, request.To, ct);
        if (resolution.NotFound) return (null, null, Results.NotFound(new ErrorResponse("not_found", "month not found")));
        if (resolution.Error is { } error) return (null, null, Results.BadRequest(error));
        var (options, invalid) = await pdf.ParseOptionsAsync(request, userId, ct);
        if (invalid is not null) return (null, null, Results.BadRequest(invalid));
        return (resolution.Period, options, null);
    }
}
