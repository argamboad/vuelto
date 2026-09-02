using System.Security.Claims;

namespace Perezosoft.Api.Services;

/// <summary>
/// Extracts user identity claims from an authenticated principal.
/// Centralizes claim extraction logic to prevent duplication.
/// </summary>
public interface IClaimsExtractor
{
    /// <summary>
    /// Extracts the provider user ID and email from the claims principal. The provider
    /// itself is known from the callback route, so it isn't derived here.
    /// </summary>
    (string? ProviderUserId, string? Email) ExtractClaims(ClaimsPrincipal principal);

    /// <summary>The user's display name from the provider, when present.</summary>
    string? ExtractDisplayName(ClaimsPrincipal principal);

    /// <summary>
    /// True ONLY when the provider explicitly asserts <c>email_verified="true"</c>; an absent,
    /// empty, or any other value reads as NOT verified (fail closed). Guards the email-match
    /// merge in <c>UserService.GetOrCreateUserAsync</c> against account takeover, so a provider
    /// that omits the claim cannot silently bypass it.
    /// </summary>
    bool IsEmailVerified(ClaimsPrincipal principal);
}
