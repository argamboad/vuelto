using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.RateLimiting;
using Perezosoft.Api.Authentication;
using Perezosoft.Api.Configuration;
using Perezosoft.Api.Services;
using Perezosoft.Core.Authorization;
using Perezosoft.Core.Entities;

namespace Perezosoft.Api.Endpoints;

/// <summary>
/// The public API surface (PUBAPI, ADR-015), split in two: <b>management</b> (owner, JWT) to mint/list/
/// revoke keys, and the <b>public</b> routes (API-key auth) a customer's systems call. Both are mapped
/// only when PUBAPI is enabled, so when it's off the routes don't exist. New public routes go on the
/// public group and declare their required scope with <c>.RequireApiScope(...)</c>.
/// </summary>
public static class ApiKeyEndpoints
{
    /// <summary>Owner-only key management (JWT). Mapped only when PUBAPI is enabled.</summary>
    public static IEndpointRouteBuilder MapApiKeyManagement(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/apikeys")
            .RequireAuthorization(AuthPolicies.TenantApi)
            .RequirePermission(Permission.ManageApiKeys) // owner-only (RBAC, ADR-009)
            .WithTags("API keys");

        group.MapGet("/", async (IApiKeyService keys, CancellationToken ct) =>
            Results.Ok((await keys.ListAsync(ct)).Select(ApiKeyResponse.From).ToList()));

        group.MapPost("/", async (CreateApiKeyRequest request, IApiKeyService keys, HttpContext http, CancellationToken ct) =>
        {
            var userId = CurrentUserId(http);
            if (userId is null)
                return Results.Unauthorized();

            var created = await keys.CreateAsync(userId.Value, request.Name ?? "", request.Scopes, request.ExpiresAt, ct);
            if (created is null)
                return Results.BadRequest(new ErrorResponse("invalid_scopes", "None of the requested scopes are recognized."));
            // The raw key is returned ONCE here and never again.
            return Results.Created($"/api/apikeys/{created.Key.Id}", ApiKeyResponse.FromCreated(created));
        });

        group.MapDelete("/{id:guid}", async (Guid id, IApiKeyService keys, CancellationToken ct) =>
            await keys.RevokeAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        return app;
    }

    /// <summary>The customer-facing public routes (API-key auth). Mapped only when PUBAPI is enabled.</summary>
    public static IEndpointRouteBuilder MapPublicApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/public")
            .RequireAuthorization(AuthPolicies.PublicApi)
            .RequireRateLimiting(RateLimiting.PublicApiPolicy) // per-key throttle (PUBAPI-2)
            .WithTags("Public API");

        // Demo (read scope): echoes the authenticated tenant + the key's scopes. Replace with real routes.
        group.MapGet("/whoami", (HttpContext http) => Results.Ok(new
        {
            tenant_id = http.User.FindFirst(JwtClaims.TenantId)?.Value,
            key = http.User.FindFirst(ApiKeyAuthenticationHandler.KeyNameClaim)?.Value,
            scopes = http.User.FindAll(ApiKeyAuthenticationHandler.ScopeClaim).Select(c => c.Value).ToArray(),
        })).RequireApiScope(ApiScopes.Read);

        // Demo (write scope): shows scope gating — a read-only key gets 403 here.
        group.MapPost("/echo", (EchoRequest request) => Results.Ok(new { echoed = request.Message ?? "" }))
            .RequireApiScope(ApiScopes.Write);

        return app;
    }

    private static Guid? CurrentUserId(HttpContext http) => http.User.GetUserId();
}

/// <summary>Gates a public endpoint on an API-key scope; a key lacking it gets 403 <c>insufficient_scope</c>.</summary>
public static class ApiScopeEndpointExtensions
{
    public static TBuilder RequireApiScope<TBuilder>(this TBuilder builder, string scope)
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(async (context, next) =>
        {
            var hasScope = context.HttpContext.User
                .FindAll(ApiKeyAuthenticationHandler.ScopeClaim).Any(c => c.Value == scope);
            if (hasScope)
                return await next(context);

            return Results.Json(new InsufficientScopeResponse(
                "insufficient_scope",
                scope,
                $"This endpoint requires the '{scope}' API-key scope."), statusCode: StatusCodes.Status403Forbidden);
        });
}

/// <summary>The public-API 403 body: the shared <c>error</c>/<c>message</c> envelope plus the missing
/// scope. A NAMED record (v3 audit TR-5/R76) so the wire contract is reviewable — byte-identical to the
/// previous anonymous shape.</summary>
public sealed record InsufficientScopeResponse(string Error, string Scope, string Message);

public sealed record CreateApiKeyRequest
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("scopes")] public string[]? Scopes { get; init; }
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; init; }
}

public sealed record EchoRequest
{
    [JsonPropertyName("message")] public string? Message { get; init; }
}

public sealed record ApiKeyResponse
{
    [JsonPropertyName("id")] public required Guid Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("prefix")] public required string Prefix { get; init; }
    [JsonPropertyName("scopes")] public required IReadOnlyList<string> Scopes { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("last_used_at")] public DateTimeOffset? LastUsedAt { get; init; }
    [JsonPropertyName("expires_at")] public DateTimeOffset? ExpiresAt { get; init; }
    [JsonPropertyName("revoked_at")] public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>The raw key — present ONLY in the create response, never on list.</summary>
    [JsonPropertyName("key")] public string? Key { get; init; }

    public static ApiKeyResponse From(ApiKey k) => new()
    {
        Id = k.Id,
        Name = k.Name,
        Prefix = k.Prefix,
        Scopes = ApiScopes.Parse(k.Scopes).ToList(),
        CreatedAt = k.CreatedAt,
        LastUsedAt = k.LastUsedAt,
        ExpiresAt = k.ExpiresAt,
        RevokedAt = k.RevokedAt,
    };

    public static ApiKeyResponse FromCreated(ApiKeyCreated created) => From(created.Key) with { Key = created.RawKey };
}
