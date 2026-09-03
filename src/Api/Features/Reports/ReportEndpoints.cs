using Microsoft.AspNetCore.Mvc;
using Vuelto.Api.Endpoints;
using Vuelto.Api.Services;

namespace Vuelto.Api.Features.Reports;

/// <summary>
/// REPORTS-1/2 routes under <c>/api/reports</c> (any household member; tenant-API policy via the group
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

        return app;
    }
}
