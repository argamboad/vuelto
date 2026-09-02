using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Perezosoft.Api.Services;

namespace Perezosoft.Api.Authentication;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions;

/// <summary>
/// Authenticates programmatic requests by API key (PUBAPI, ADR-015) — the second authentication scheme
/// alongside JWT Bearer. A key is presented in <c>X-Api-Key</c> (or <c>Authorization: Bearer pk_…</c>);
/// it's resolved to a tenant + scopes and turned into a principal carrying the <c>tenant_id</c> claim, so
/// the normal tenant query filter scopes everything to the key's tenant with no extra wiring. No key ⇒
/// NoResult (anonymous / other schemes); a bad key ⇒ Fail (401).
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string ScopeClaim = "scope";
    public const string KeyNameClaim = "api_key_name";
    private const string HeaderName = "X-Api-Key";
    private const string RawPrefix = "pk_";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var raw = ExtractKey();
        if (string.IsNullOrEmpty(raw))
            return AuthenticateResult.NoResult(); // no key presented — let anonymous / other schemes handle

        var service = Context.RequestServices.GetRequiredService<IApiKeyService>();
        var result = await service.AuthenticateAsync(raw, Context.RequestAborted);
        if (result is null)
            return AuthenticateResult.Fail("Invalid or expired API key.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, result.KeyId.ToString()), // actor for auditing = the key
            new(JwtClaims.TenantId, result.TenantId.ToString()),     // drives HttpCurrentTenant scoping
            new(KeyNameClaim, result.Name),
        };
        claims.AddRange(result.Scopes.Select(s => new Claim(ScopeClaim, s)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private string? ExtractKey()
    {
        if (Request.Headers.TryGetValue(HeaderName, out var header) && !string.IsNullOrWhiteSpace(header))
            return header.ToString().Trim();

        // Also accept the key as a Bearer token — but only if it's an API key (pk_…), so JWTs fall through.
        var authorization = Request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
        {
            var token = authorization[bearer.Length..].Trim();
            if (token.StartsWith(RawPrefix, StringComparison.Ordinal))
                return token;
        }
        return null;
    }
}
