using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using Vuelto.Api.Configuration;
using Vuelto.Api.Models;
using Vuelto.Api.Services;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure;
using Vuelto.Infrastructure.Email;

namespace Vuelto.Api.Controllers;

/// <summary>
/// Authentication controller for the OAuth → JWT + refresh-token flow.
/// Focused solely on HTTP request/response handling; delegates to services.
/// </summary>
[ApiController]
[Route("api/auth")]
public class AuthController(
    IUserService userService,
    ISessionService sessionService,
    IRefreshTokenService refreshTokenService,
    ICookieService cookieService,
    IClaimsExtractor claimsExtractor,
    IProviderEmailTrust providerEmailTrust,
    IPasswordlessService passwordless,
    ILinkTokenService linkTokenService,
    IEmailSender emailSender,
    IApplicationSettings appSettings,
    IPasswordlessSettings passwordlessSettings,
    IMfaLoginService mfaLogin,
    ILogger<AuthController> logger) : AuthControllerBase
{
    /// <summary>
    /// The OAuth providers this deployment has actually configured (lowercase keys, e.g. "google").
    /// Anonymous — the login page reads it pre-auth to render only the providers that will work, instead
    /// of a dead button that 500s on challenge. Empty when none are configured.
    /// </summary>
    [HttpGet("providers")]
    [AllowAnonymous]
    public async Task<IActionResult> Providers([FromServices] IAuthenticationSchemeProvider schemeProvider) =>
        Ok(new { providers = await AuthProviders.EnabledAsync(schemeProvider) });

    /// <summary>
    /// Starts the OAuth flow: challenges the matching scheme. The callback route
    /// carries the provider so the callback can resolve the right identity.
    /// </summary>
    [HttpGet("login/{provider}")]
    public IActionResult Login(string provider, [FromQuery(Name = "link_token")] string? linkToken = null)
    {
        provider = provider.ToLowerInvariant();
        // A link token (issued to an already-signed-in user) rides through the
        // round-trip so the callback can attach the identity instead of signing in.
        var redirectUri = string.IsNullOrEmpty(linkToken)
            ? $"/api/auth/callback/{provider}"
            : $"/api/auth/callback/{provider}?link_token={Uri.EscapeDataString(linkToken)}";
        var properties = new AuthenticationProperties { RedirectUri = redirectUri };

        var scheme = AuthProviders.SchemeFor(provider);
        return scheme is null
            ? Redirect($"{appSettings.ClientUrl}/auth-error")
            : Challenge(properties, scheme);
    }

    /// <summary>
    /// OAuth callback handler — runs after the provider redirects back. Reads the
    /// external principal (carried in the External cookie scheme), resolves/creates
    /// the account, issues a refresh-token cookie, and redirects to the client.
    /// The JWT is never placed in the URL (it would leak via history/logs).
    /// </summary>
    [HttpGet("callback/{provider}")]
    [Authorize(AuthenticationSchemes = ServiceCollectionExtensions.ExternalScheme)]
    public async Task<IActionResult> Callback(string provider, CancellationToken cancellationToken, [FromQuery(Name = "link_token")] string? linkToken = null)
    {
        try
        {
            provider = provider.ToLowerInvariant();
            if (!AuthProviders.IsSupported(provider))
            {
                logger.LogWarning("OAuth callback for unsupported provider: {Provider}", provider);
                return Redirect($"{appSettings.ClientUrl}/auth-error");
            }

            var (providerUserId, email) = claimsExtractor.ExtractClaims(User);

            if (string.IsNullOrEmpty(providerUserId) || string.IsNullOrEmpty(email))
            {
                logger.LogWarning("OAuth callback: missing claims");
                return Redirect($"{appSettings.ClientUrl}/auth-error");
            }

            // LINK MODE: attach this identity to the initiating account, don't sign in.
            if (!string.IsNullOrEmpty(linkToken))
            {
                var linkUserId = linkTokenService.Redeem(linkToken);
                await HttpContext.SignOutAsync(ServiceCollectionExtensions.ExternalScheme);

                if (linkUserId is null)
                    return Redirect($"{appSettings.ClientUrl}/settings?link_error=expired");

                var linkResult = await userService.LinkLoginAsync(linkUserId.Value, provider, providerUserId, cancellationToken);
                logger.LogInformation("Link {Provider} to user {UserId}: {Result}", provider, linkUserId, linkResult);
                return linkResult == LinkLoginResult.OwnedByAnotherAccount
                    ? Redirect($"{appSettings.ClientUrl}/settings?link_error=in_use")
                    : Redirect($"{appSettings.ClientUrl}/settings?linked={provider}");
            }

            var user = await userService.GetOrCreateUserAsync(email, providerUserId, provider,
                claimsExtractor.ExtractDisplayName(User), EmailVerifiedForMerge(provider), cancellationToken);

            // Sign the external carrier cookie out — its job is done.
            await HttpContext.SignOutAsync(ServiceCollectionExtensions.ExternalScheme);

            // MFA step-up (MFA-2/3, ADR-012): a user with MFA enabled gets a signed challenge instead of
            // a session — bounce to the client's login step-up (which posts to /mfa/verify) rather than
            // completing sign-in here. Without MFA, issue the session exactly as before. Routing through
            // CompleteOrChallengeAsync is what stops OAuth from silently bypassing the second factor.
            var (session, challenge) = await mfaLogin.CompleteOrChallengeAsync(user, provider, ClientIp, native: false, cancellationToken);
            if (challenge is not null)
                return Redirect($"{appSettings.ClientUrl}/login?mfa={Uri.EscapeDataString(challenge)}");

            cookieService.SetRefreshTokenCookie(Response, session!.RefreshToken, Request);
            logger.LogInformation("OAuth callback successful for user: {Email} via {Provider}", email, provider);
            return Redirect($"{appSettings.ClientUrl}/auth-callback");
        }
        catch (UnverifiedEmailConflictException)
        {
            return Redirect($"{appSettings.ClientUrl}/login?error=email_unverified");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OAuth callback failed");
            return Redirect($"{appSettings.ClientUrl}/auth-error");
        }
    }

    /// <summary>
    /// Exchanges a refresh token for a fresh access token, rotating the refresh token: the used
    /// token is revoked and a new one issued. If an already-rotated (revoked) token is later
    /// replayed, that's treated as token theft — every session for the user is revoked and the
    /// event is audit-logged (the client sees the same generic error as any invalid token, so the
    /// reuse signal isn't leaked). Transport depends on the client: the browser sends/receives the
    /// token via the HttpOnly cookie; a native client (header <c>X-Native-Client: true</c>) sends it
    /// in the body and gets the rotated token back in the body — it never had a cookie to begin with.
    /// </summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh(
        CancellationToken cancellationToken,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RefreshRequest? req = null)
    {
        try
        {
            var native = IsNativeClient;
            var rawToken = native ? req?.RefreshToken : cookieService.GetRefreshTokenFromCookies(Request);
            if (string.IsNullOrEmpty(rawToken))
                return Unauthorized(new ErrorResponse("no_refresh_token", "Refresh token not found"));

            var inspection = await refreshTokenService.InspectRefreshTokenAsync(rawToken, cancellationToken);
            if (inspection.Status == RefreshTokenStatus.Reuse)
            {
                // Replay of a rotated-out token ⇒ assume theft: revoke every session for the user.
                // Client still gets the generic error below, so the reuse signal isn't leaked.
                await refreshTokenService.RevokeAllUserTokensAsync(inspection.Token!.UserId, cancellationToken);
                logger.LogWarning("Refresh-token reuse detected for user {UserId}; revoked all sessions", inspection.Token.UserId);
            }
            if (inspection.Status != RefreshTokenStatus.Valid)
                return Unauthorized(new ErrorResponse("invalid_refresh_token", "Refresh token is invalid or expired"));

            var validToken = inspection.Token!;
            var user = await userService.GetUserByIdAsync(validToken.UserId, cancellationToken);
            if (user == null)
                return Unauthorized(new ErrorResponse("user_not_found", "User not found"));

            // Rotate: revoke the used token, then issue a fresh session.
            await refreshTokenService.RevokeRefreshTokenAsync(validToken.Id, cancellationToken);

            var session = await sessionService.IssueAsync(user, validToken.Provider, ClientIp, native, cancellationToken);

            // Web: rotate the cookie. Native: the rotated token is already on the body.
            if (!native)
                cookieService.SetRefreshTokenCookie(Response, session.RefreshToken, Request);

            logger.LogInformation("Token refreshed for user: {UserId}", validToken.UserId);

            return Ok(session.Response);
        }
        // Let client-disconnect cancellation propagate (request aborted) instead of masking it as a 500.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Token refresh failed");
            return StatusCode(500, new ErrorResponse("refresh_failed", "Failed to refresh token"));
        }
    }

    /// <summary>
    /// Revokes all refresh tokens for the session and deletes the cookie.
    /// Identifies the user by the refresh cookie (not [Authorize]) so it works
    /// even with an expired access token. Idempotent.
    /// </summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(
        CancellationToken cancellationToken,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] RefreshRequest? req = null)
    {
        try
        {
            var native = IsNativeClient;
            var rawToken = native ? req?.RefreshToken : cookieService.GetRefreshTokenFromCookies(Request);
            if (!string.IsNullOrEmpty(rawToken))
            {
                var token = await refreshTokenService.ValidateRefreshTokenAsync(rawToken, cancellationToken);
                if (token != null)
                {
                    await refreshTokenService.RevokeAllUserTokensAsync(token.UserId, cancellationToken);
                    logger.LogInformation("User logout: {UserId}", token.UserId);
                }
            }

            // Native clients have no cookie to clear; they drop the token from secure storage.
            if (!native)
                cookieService.DeleteRefreshTokenCookie(Response);
            return Ok(new { message = "Logged out successfully" });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Logout failed");
            return StatusCode(500, new ErrorResponse("logout_failed", "Failed to logout"));
        }
    }

    // ── Passwordless: magic link (web) ───────────────────────────────────────

    /// <summary>
    /// Emails a single-use sign-in link. Always returns 200 — it never reveals
    /// whether an account exists for the address.
    /// </summary>
    [HttpPost("magic-link/send")]
    [EnableRateLimiting(RateLimiting.PasswordlessPolicy)]
    public async Task<IActionResult> SendMagicLink([FromBody] EmailRequest req, CancellationToken cancellationToken)
    {
        if (!IsLikelyEmail(req.Email))
            return BadRequest(new ErrorResponse("invalid_email", "A valid email address is required."));

        var email = req.Email.Trim();
        var token = await passwordless.IssueMagicLinkTokenAsync(email, cancellationToken);
        var link = $"{Request.Scheme}://{Request.Host}/api/auth/magic-link/verify" +
                   $"?token={Uri.EscapeDataString(token)}&email={Uri.EscapeDataString(email)}";

        var emailBody = BrandedEmail.MagicLink(link, passwordlessSettings.MagicLinkLifespanMinutes,
            BrandedEmail.ResolveCulture(req.Culture));
        await emailSender.SendAsync(email, emailBody.Subject, emailBody.Html, emailBody.InlineImages, cancellationToken);

        return Ok();
    }

    /// <summary>
    /// Validates a magic-link token, establishes the session (refresh cookie), and
    /// bounces to the client callback. The JWT is never put in the URL.
    /// </summary>
    [HttpGet("magic-link/verify")]
    public async Task<IActionResult> VerifyMagicLink([FromQuery] string token, [FromQuery] string email, CancellationToken cancellationToken)
    {
        var user = await passwordless.RedeemMagicLinkAsync(email, token, cancellationToken);
        if (user is null)
            return Redirect($"{appSettings.ClientUrl}/login?error=invalid_link");

        // MFA step-up (MFA-2/3, ADR-012): challenge instead of a session when the user has MFA on, so a
        // magic link can't bypass the second factor. The client posts the challenge + code to /mfa/verify.
        var (session, challenge) = await mfaLogin.CompleteOrChallengeAsync(user, LoginTokenPurpose.MagicLink, ClientIp, native: false, cancellationToken);
        if (challenge is not null)
            return Redirect($"{appSettings.ClientUrl}/login?mfa={Uri.EscapeDataString(challenge)}");

        cookieService.SetRefreshTokenCookie(Response, session!.RefreshToken, Request);
        return Redirect($"{appSettings.ClientUrl}/auth-callback");
    }

    // ── Passwordless: OTP (web + future mobile) ──────────────────────────────

    /// <summary>Emails a single-use numeric code. Always returns 200 (no enumeration).</summary>
    [HttpPost("otp/send")]
    [EnableRateLimiting(RateLimiting.PasswordlessPolicy)]
    public async Task<IActionResult> SendOtp([FromBody] EmailRequest req, CancellationToken cancellationToken)
    {
        if (!IsLikelyEmail(req.Email))
            return BadRequest(new ErrorResponse("invalid_email", "A valid email address is required."));

        var email = req.Email.Trim();
        var code = await passwordless.IssueOtpAsync(email, cancellationToken);

        var emailBody = BrandedEmail.Otp(code, passwordlessSettings.OtpLifespanMinutes,
            BrandedEmail.ResolveCulture(req.Culture));
        await emailSender.SendAsync(email, emailBody.Subject, emailBody.Html, emailBody.InlineImages, cancellationToken);

        return Ok();
    }

    /// <summary>
    /// Verifies an OTP code and establishes the session. The browser gets a refresh
    /// cookie; a native client (header <c>X-Native-Client: true</c>) gets the refresh
    /// token in the body to persist in its OS secure store. Both get the access token.
    /// </summary>
    [HttpPost("otp/verify")]
    [EnableRateLimiting(RateLimiting.PasswordlessVerifyPolicy)]
    public async Task<IActionResult> VerifyOtp([FromBody] OtpVerifyRequest req, CancellationToken cancellationToken)
    {
        var result = await passwordless.RedeemOtpAsync(req.Email, req.Code, cancellationToken);
        if (result.Status != OtpStatus.Success || result.User is null)
        {
            // Collapse "no active code" and "wrong code" to one client error so the response can't
            // be used to probe whether an address has an outstanding OTP (CONF-6).
            var code = OtpErrors.ClientCode(result.Status);
            return Unauthorized(new ErrorResponse(code, "The code is incorrect or has expired."));
        }

        var native = IsNativeClient;
        var (session, challenge) = await mfaLogin.CompleteOrChallengeAsync(
            result.User, LoginTokenPurpose.Otp, ClientIp, native, cancellationToken);
        if (challenge is not null)
            return Ok(new MfaRequiredResponse { Challenge = challenge });

        if (!native)
            cookieService.SetRefreshTokenCookie(Response, session!.RefreshToken, Request);
        return Ok(session!.Response);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // Whether to treat the provider's email as verified for auto-linking to an existing same-email
    // account: the explicit email_verified="true" claim (fail-closed default, MITI-3) OR a provider
    // trusted by IProviderEmailTrust (e.g. Microsoft on the consumers tenant, which verifies the email
    // but omits the claim). Untrusted/unknown providers still require the claim.
    private bool EmailVerifiedForMerge(string provider) =>
        claimsExtractor.IsEmailVerified(User) || providerEmailTrust.TrustsEmailWithoutClaim(provider);
}
