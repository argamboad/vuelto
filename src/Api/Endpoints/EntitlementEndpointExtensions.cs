using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Api.Configuration;
using Vuelto.Core.Abstractions;

namespace Vuelto.Api.Endpoints;

/// <summary>
/// Gates an endpoint (or a feature group) behind a plan entitlement (ADR-006). A feature slice adds
/// <c>.RequireEntitlement(Entitlements.Xxx)</c> alongside <see cref="FeatureEndpointExtensions.MapTenantFeatureGroup"/>
/// to declare a paid gate; a tenant whose plan lacks the entitlement gets <b>402 Payment Required</b>
/// (not 403/500), with the entitlement named and an upgrade link. The check is server-side via
/// <see cref="IEntitlementService"/> — never trust the client.
/// </summary>
public static class EntitlementEndpointExtensions
{
    public static TBuilder RequireEntitlement<TBuilder>(this TBuilder builder, string entitlementKey)
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(async (context, next) =>
        {
            var entitlements = context.HttpContext.RequestServices.GetRequiredService<IEntitlementService>();
            if (await entitlements.HasAsync(entitlementKey, context.HttpContext.RequestAborted))
                return await next(context);

            var app = context.HttpContext.RequestServices.GetRequiredService<IApplicationSettings>();
            return Results.Json(new PaymentRequiredResponse(
                "payment_required",
                entitlementKey,
                $"This feature requires a plan that includes '{entitlementKey}'.",
                $"{app.ClientUrl.TrimEnd('/')}/billing"), statusCode: StatusCodes.Status402PaymentRequired);
        });
}

/// <summary>The 402 body: the shared <c>error</c>/<c>message</c> envelope plus which entitlement was
/// missing and where to upgrade. A NAMED record (v3 audit TR-5/R76) so the wire contract is reviewable —
/// byte-identical to the previous anonymous shape (incl. the snake_case <c>upgrade_url</c>).</summary>
public sealed record PaymentRequiredResponse(
    string Error,
    string Entitlement,
    string Message,
    [property: System.Text.Json.Serialization.JsonPropertyName("upgrade_url")] string UpgradeUrl);
