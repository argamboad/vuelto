using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Api.Authentication;

namespace Vuelto.Api.Configuration;

/// <summary>
/// The named authorization policies. <see cref="TenantApi"/> is the JWT-authenticated tenant policy shared
/// by platform controllers (<c>TenantApiControllerBase</c>) and feature minimal-API groups
/// (<c>MapTenantFeatureGroup</c>), so the "authenticated via the app JWT" rule is defined once (CONF-3).
/// <see cref="PublicApi"/> is the API-key-authenticated policy for the public surface (PUBAPI, ADR-015).
/// </summary>
public static class AuthPolicies
{
    public const string TenantApi = "TenantApi";
    public const string PublicApi = "PublicApi";

    public static IServiceCollection AddTenantApiAuthorization(this IServiceCollection services) =>
        services.AddAuthorization(options =>
        {
            options.AddPolicy(TenantApi, policy => policy
                .RequireAuthenticatedUser()
                .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme));

            // Public API: authenticated by an API key (the ApiKey scheme). Only reachable when PUBAPI is
            // enabled (the routes aren't mapped otherwise); the policy is harmless when unused.
            options.AddPolicy(PublicApi, policy => policy
                .RequireAuthenticatedUser()
                .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName));
        });
}
