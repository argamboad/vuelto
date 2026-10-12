using Vuelto.Api.Endpoints;
using Vuelto.Api.Services;
using Vuelto.Core.Budget;

namespace Vuelto.Api.Features.ExchangeRate;

/// <summary>
/// FX-1 route: <c>GET /api/exchange-rate</c> — the resolved USD→CRC rate for the new-transaction form
/// and the Home badge. Any member may read (the group helper applies the tenant-API policy). When the
/// whole chain comes up empty the answer is 503 <c>exchange_rate_unavailable</c> with the shared error
/// shape — never a fabricated rate (ADR-V006).
/// </summary>
public static class ExchangeRateEndpoints
{
    public const string UnavailableMessage = "No exchange rate available — try again later or enter one manually";

    public static IEndpointRouteBuilder MapExchangeRate(this IEndpointRouteBuilder app)
    {
        var group = app.MapTenantFeatureGroup("/api/exchange-rate");

        group.MapGet("/", async (IExchangeRateResolver resolver, CancellationToken ct) =>
        {
            var resolved = await resolver.ResolveAsync(ct);
            return resolved is null
                ? Results.Json(new ErrorResponse("exchange_rate_unavailable", UnavailableMessage), statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(ExchangeRateResponse.From(resolved));
        });

        return app;
    }
}
