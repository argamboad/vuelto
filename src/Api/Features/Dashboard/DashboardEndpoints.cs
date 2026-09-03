using Vuelto.Api.Endpoints;
using Vuelto.Api.Services;

namespace Vuelto.Api.Features.Dashboard;

/// <summary>
/// DASH-1 route: <c>GET /api/months/{id}/summary</c> — the month header, the resolved rate (or
/// <c>rate_unavailable</c>) and the dashboard summary. Any household member; the group helper applies
/// the tenant-API policy. Lives in its own slice: the Ledger owns the month, the Dashboard reads it.
/// </summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboard(this IEndpointRouteBuilder app)
    {
        var group = app.MapTenantFeatureGroup("/api/months/{id:guid}/summary");

        group.MapGet("/", async (Guid id, DashboardHandler handler, CancellationToken ct) =>
        {
            var dashboard = await handler.GetAsync(id, ct);
            return dashboard is null ? Results.NotFound(new ErrorResponse("not_found", "month not found")) : Results.Ok(dashboard);
        });

        return app;
    }
}
