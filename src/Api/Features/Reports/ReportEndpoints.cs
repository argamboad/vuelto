using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Vuelto.Api.Endpoints;
using Vuelto.Api.Features.Reports.Pdf;
using Vuelto.Api.Services;

namespace Vuelto.Api.Features.Reports;

/// <summary>
/// REPORTS-1/2/7 routes under <c>/api/reports</c> (any household member; tenant-API policy via the group
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
            ReportHandler handler, ReportPdfHandler pdf, CancellationToken ct) =>
        {
            var request = body ?? new ReportPdfRequest();
            var resolution = await handler.ResolvePeriodAsync(request.MonthId, request.From, request.To, ct);
            if (resolution.NotFound) return Results.NotFound(new ErrorResponse("not_found", "month not found"));
            if (resolution.Error is { } error) return Results.BadRequest(error);
            var (options, invalid) = pdf.ParseOptions(request);
            if (invalid is not null) return Results.BadRequest(invalid);
            return Results.Ok(await pdf.CreateAsync(resolution.Period!, options!, ct));
        });

        return app;
    }
}
