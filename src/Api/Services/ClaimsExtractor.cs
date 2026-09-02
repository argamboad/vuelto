using System.Security.Claims;

namespace Vuelto.Api.Services;

/// <summary>
/// Extracts claims from an authenticated principal, provider-agnostically.
/// </summary>
public class ClaimsExtractor : IClaimsExtractor
{
    public (string? ProviderUserId, string? Email) ExtractClaims(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // The provider is known from the callback route, so it isn't derived here — that
        // avoids guessing it from issuer claims (which mis-attributes once a 3rd provider
        // is added).
        var providerUserId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = principal.FindFirst(ClaimTypes.Email)?.Value
                   ?? principal.FindFirst("preferred_username")?.Value;

        return (providerUserId, email);
    }

    public string? ExtractDisplayName(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        var name = principal.FindFirst(ClaimTypes.Name)?.Value
                   ?? principal.FindFirst("name")?.Value;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    public bool IsEmailVerified(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        // Fail closed: verified ONLY when the provider explicitly asserts email_verified="true".
        // Absent / empty / any other value ⇒ not verified. An absent claim must not be trusted —
        // not every provider asserts it (some Microsoft configs omit it), and the platform
        // advertises "new OAuth provider = one line", so a silent absent-⇒-verified default would
        // hand any such provider the account-takeover merge path (see UserService takeover guard).
        var claim = principal.FindFirst("email_verified")?.Value;
        return string.Equals(claim, "true", StringComparison.OrdinalIgnoreCase);
    }
}
