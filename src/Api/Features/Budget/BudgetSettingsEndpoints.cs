using Vuelto.Api.Endpoints;

namespace Vuelto.Api.Features.Budget;

/// <summary>
/// BUDGET-1 routes: <c>GET</c> / <c>PUT /api/budget-settings</c>. Any household member may read and
/// save (budget data is the member baseline — ADR-V002), so no <c>RequirePermission</c> filter; the
/// group helper applies the tenant-API auth policy. Errors use the shared <c>ErrorResponse</c>.
/// </summary>
public static class BudgetSettingsEndpoints
{
    public static IEndpointRouteBuilder MapBudgetSettings(this IEndpointRouteBuilder app)
    {
        var group = app.MapTenantFeatureGroup("/api/budget-settings");

        group.MapGet("/", async (BudgetSettingsHandler handler, CancellationToken ct) =>
        {
            var current = await handler.GetAsync(ct);
            return current is null ? Results.Unauthorized() : Results.Ok(current);
        });

        group.MapPut("/", async (UpdateBudgetSettingsRequest request, BudgetSettingsHandler handler, CancellationToken ct) =>
        {
            var (saved, error) = await handler.UpdateAsync(request, ct);
            if (error is not null)
                return error.Error == "invalid_token"
                    ? Results.Json(error, statusCode: StatusCodes.Status401Unauthorized)
                    : Results.BadRequest(error);
            return Results.Ok(saved);
        });

        return app;
    }
}
