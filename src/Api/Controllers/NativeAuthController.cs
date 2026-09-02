using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vuelto.Api.Configuration;
using Vuelto.Api.Models;
using Vuelto.Api.Services;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure;

namespace Vuelto.Api.Controllers;

/// <summary>
/// Native (desktop/mobile) OAuth: loopback / custom-scheme one-time-code flow.
/// </summary>
[ApiController]
[Route("api/auth")]
public class NativeAuthController(
    IUserService userService,
    INativeAuthCodeService nativeAuthCodeService,
    IClaimsExtractor claimsExtractor,
    IProviderEmailTrust providerEmailTrust,
    ILinkTokenService linkTokenService,
    IApplicationSettings appSettings,
    IMfaLoginService mfaLogin,
    ILogger<NativeAuthController> logger) : AuthControllerBase
{
    /// <summary>
    /// Starts OAuth for a native client. The app opens this URL in the system browser
    /// (passing a loopback <paramref name="redirect"/> it's listening on); the provider
    /// round-trip lands on the native callback, which hands back a one-time code.
    /// </summary>
    [HttpGet("native/login/{provider}")]
    public IActionResult NativeLogin(string provider, [FromQuery] string redirect,
        [FromQuery(Name = "link_token")] string? linkToken = null, [FromQuery] string? state = null)
    {
        provider = provider.ToLowerInvariant();
        if (!AuthProviders.IsSupported(provider) || !IsAllowedNativeRedirect(redirect))
            return BadRequest(new ErrorResponse("invalid_request", "Unsupported provider or redirect target."));

        // Thread the client's CSRF state through the provider round-trip so the callback can echo it back
        // for the client to validate (v3 NAT-9). Optional end-to-end — an older client sends none.
        var callback = NativeAuthUrls.Callback(provider, redirect, linkToken, state);
        var properties = new AuthenticationProperties { RedirectUri = callback };

        var scheme = AuthProviders.SchemeFor(provider);
        return scheme is null
            ? BadRequest(new ErrorResponse("invalid_request", "Unsupported provider."))
            : Challenge(properties, scheme);
    }

    /// <summary>
    /// Native OAuth callback. Resolves/creates the account, mints a single-use code,
    /// and redirects to the app's loopback/scheme URL carrying ONLY that code — tokens
    /// never travel in the URL. The app exchanges the code at /native/exchange.
    /// </summary>
    [HttpGet("native/callback/{provider}")]
    [Authorize(AuthenticationSchemes = ServiceCollectionExtensions.ExternalScheme)]
    public async Task<IActionResult> NativeCallback(string provider, [FromQuery] string redirect,
        CancellationToken cancellationToken, [FromQuery(Name = "link_token")] string? linkToken = null,
        [FromQuery] string? state = null)
    {
        provider = provider.ToLowerInvariant();
        // Echo the client's CSRF state on EVERY outcome so it can be validated before the code is used (NAT-9).
        string To(string key, string value) => NativeAuthUrls.ClientRedirect(redirect, key, value, state);
        try
        {
            if (!AuthProviders.IsSupported(provider) || !IsAllowedNativeRedirect(redirect))
                return BadRequest(new ErrorResponse("invalid_request", "Unsupported provider or redirect target."));

            var (providerUserId, email) = claimsExtractor.ExtractClaims(User);
            await HttpContext.SignOutAsync(ServiceCollectionExtensions.ExternalScheme);

            if (string.IsNullOrEmpty(providerUserId) || string.IsNullOrEmpty(email))
                return Redirect(To("error", "auth_failed"));

            // LINK MODE: attach this identity to the initiating account, don't sign in.
            if (!string.IsNullOrEmpty(linkToken))
            {
                var linkUserId = linkTokenService.Redeem(linkToken);
                if (linkUserId is null)
                    return Redirect(To("error", "expired"));

                var linkResult = await userService.LinkLoginAsync(linkUserId.Value, provider, providerUserId, cancellationToken);
                logger.LogInformation("Native link {Provider} to user {UserId}: {Result}", provider, linkUserId, linkResult);
                return linkResult == LinkLoginResult.OwnedByAnotherAccount
                    ? Redirect(To("error", "in_use"))
                    : Redirect(To("linked", provider));
            }

            var user = await userService.GetOrCreateUserAsync(email, providerUserId, provider,
                claimsExtractor.ExtractDisplayName(User), EmailVerifiedForMerge(provider), cancellationToken);

            var code = nativeAuthCodeService.Issue(user.Id, provider);
            logger.LogInformation("Native OAuth callback successful for {Email} via {Provider}", email, provider);
            return Redirect(To("code", code));
        }
        catch (UnverifiedEmailConflictException)
        {
            return Redirect(To("error", "email_unverified"));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Native OAuth callback failed");
            return Redirect(To("error", "auth_failed"));
        }
    }

    /// <summary>
    /// Exchanges a single-use native-auth code for an access token + refresh token
    /// (both in the body). The code is consumed on first use.
    /// </summary>
    [HttpPost("native/exchange")]
    public async Task<IActionResult> NativeExchange([FromBody] NativeExchangeRequest req, CancellationToken cancellationToken)
    {
        var grant = nativeAuthCodeService.Redeem(req.Code);
        if (grant is null)
            return Unauthorized(new ErrorResponse("invalid_code", "The code is invalid or has expired."));

        var user = await userService.GetUserByIdAsync(grant.Value.UserId, cancellationToken);
        if (user is null)
            return Unauthorized(new ErrorResponse("user_not_found", "User not found"));

        // Native exchange always returns the refresh token in the body.
        var (session, challenge) = await mfaLogin.CompleteOrChallengeAsync(
            user, grant.Value.Provider, ClientIp, native: true, cancellationToken);
        return challenge is not null
            ? Ok(new MfaRequiredResponse { Challenge = challenge })
            : Ok(session!.Response);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether the native client's redirect target is permitted. Loopback HTTP
    /// (127.0.0.1 / localhost, any port) is always allowed — the desktop pattern
    /// (RFC 8252 §7.3). A configured custom scheme (mobile) is allowed too. Anything
    /// else is rejected to prevent the callback being used as an open redirect.
    /// </summary>
    private bool IsAllowedNativeRedirect(string? redirect) =>
        NativeRedirectPolicy.IsAllowed(redirect, appSettings.NativeCallbackScheme);

    // Whether to treat the provider's email as verified for auto-linking to an existing same-email
    // account: the explicit email_verified="true" claim (fail-closed default, MITI-3) OR a provider
    // trusted by IProviderEmailTrust (e.g. Microsoft on the consumers tenant, which verifies the email
    // but omits the claim). Untrusted/unknown providers still require the claim.
    private bool EmailVerifiedForMerge(string provider) =>
        claimsExtractor.IsEmailVerified(User) || providerEmailTrust.TrustsEmailWithoutClaim(provider);
}
