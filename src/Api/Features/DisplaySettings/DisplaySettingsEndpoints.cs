using System.Security.Claims;
using Vuelto.Api.Authentication;
using Vuelto.Api.Endpoints;
using Vuelto.Api.Services;

namespace Vuelto.Api.Features.DisplaySettings;

/// <summary>
/// DISPLAY-1 routes: <c>GET</c> / <c>PUT /api/display-settings</c> — the caller's own display preference (user-keyed,
/// ADR-V020). Any signed-in member may read and save. Like the platform's theme and locale, a write from an
/// impersonation session is refused (403 <c>impersonation_not_allowed</c>): staff looking through a member's eyes
/// must not change what that member sees.
/// </summary>
public static class DisplaySettingsEndpoints
{
    public static IEndpointRouteBuilder MapDisplaySettings(this IEndpointRouteBuilder app)
    {
        var group = app.MapTenantFeatureGroup("/api/display-settings");

        group.MapGet("/", async (ClaimsPrincipal user, DisplaySettingsHandler handler, CancellationToken ct) =>
        {
            if (user.GetUserId() is not { } uid) return Results.Unauthorized();
            return Results.Ok(await handler.GetAsync(uid, ct));
        });

        group.MapPut("/", async (ClaimsPrincipal user, UpdateDisplaySettingsRequest request, DisplaySettingsHandler handler, CancellationToken ct) =>
        {
            if (user.GetUserId() is not { } uid) return Results.Unauthorized();
            if (user.IsImpersonation())
                return Results.Json(new ErrorResponse("impersonation_not_allowed", "Preferences cannot be changed from an impersonation session"), statusCode: StatusCodes.Status403Forbidden);
            var (saved, error) = await handler.UpdateAsync(uid, request, ct);
            return error is not null ? Results.BadRequest(error) : Results.Ok(saved);
        });

        return app;
    }
}
