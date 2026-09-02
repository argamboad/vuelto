using Vuelto.Api.Configuration;

namespace Vuelto.Api.Endpoints;

/// <summary>
/// Shared scaffolding for vertical-slice feature endpoints. <see cref="MapTenantFeatureGroup"/> is
/// the one way a feature registers its routes: it returns a <c>MapGroup</c> already guarded by the
/// shared <see cref="AuthPolicies.TenantApi"/> policy, so a slice can't forget to require auth and
/// every feature is authenticated/tenant-scoped the same way (CONF-3). Use it instead of a raw
/// <c>app.MapGroup(prefix).RequireAuthorization(...)</c>.
/// </summary>
public static class FeatureEndpointExtensions
{
    public static RouteGroupBuilder MapTenantFeatureGroup(this IEndpointRouteBuilder app, string prefix) =>
        app.MapGroup(prefix).RequireAuthorization(AuthPolicies.TenantApi);
}
