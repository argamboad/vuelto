using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Perezosoft.Api.Configuration;
using Perezosoft.Api.Models;
using Perezosoft.Api.Services;
using Perezosoft.Core.Abstractions;

namespace Perezosoft.Api.Controllers;

/// <summary>
/// MFA (authenticator-app TOTP) management + login step-up (MFA-1/2, ADR-012).
/// </summary>
[ApiController]
[Route("api/auth")]
public class MfaController(
    IMfaService mfa,
    IMfaLoginService mfaLogin,
    ICookieService cookieService) : AuthControllerBase
{
    // ── MFA: authenticator-app TOTP (MFA-1, ADR-012) ─────────────────────────

    /// <summary>Whether the signed-in user has MFA enabled.</summary>
    [HttpGet("mfa")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<IActionResult> MfaStatus(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        return Ok(new MfaStatusResponse { Enabled = await mfa.IsEnabledAsync(userId, cancellationToken) });
    }

    /// <summary>Begins TOTP enrollment: returns the provisioning URI + secret (not yet enabled).</summary>
    [HttpPost("mfa/enroll")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<IActionResult> MfaEnroll(CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var enrollment = await mfa.BeginEnrollmentAsync(userId, cancellationToken);
        return enrollment is null
            ? Unauthorized(new ErrorResponse("user_not_found", "User not found"))
            : Ok(new MfaEnrollResponse { ProvisioningUri = enrollment.ProvisioningUri, Secret = enrollment.Secret });
    }

    /// <summary>Confirms enrollment with a code: enables MFA and returns one-time recovery codes.</summary>
    [HttpPost("mfa/confirm")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<IActionResult> MfaConfirm([FromBody] MfaCodeRequest req, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var (result, recoveryCodes) = await mfa.ConfirmEnrollmentAsync(userId, req.Code ?? "", cancellationToken);
        return result switch
        {
            MfaConfirmResult.Enabled => Ok(new MfaRecoveryCodesResponse { RecoveryCodes = recoveryCodes }),
            MfaConfirmResult.NotEnrolled => BadRequest(new ErrorResponse("not_enrolled", "Start enrollment first")),
            _ => BadRequest(new ErrorResponse("invalid_code", "That code is not valid")),
        };
    }

    /// <summary>Disables MFA (requires a valid TOTP or recovery code).</summary>
    [HttpPost("mfa/disable")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<IActionResult> MfaDisable([FromBody] MfaCodeRequest req, CancellationToken cancellationToken)
    {
        if (!TryGetUserId(out var userId)) return Unauthorized();
        var result = await mfa.DisableAsync(userId, req.Code ?? "", cancellationToken);
        return result switch
        {
            MfaDisableResult.Disabled => NoContent(),
            MfaDisableResult.NotEnabled => BadRequest(new ErrorResponse("mfa_not_enabled", "MFA is not enabled")),
            _ => BadRequest(new ErrorResponse("invalid_code", "That code is not valid")),
        };
    }

    /// <summary>
    /// Completes an MFA step-up (MFA-2, ADR-012): verifies the challenge from a login + a TOTP or
    /// recovery code, and establishes the session (browser gets the refresh cookie; native gets it in
    /// the body). Any bad/expired challenge or wrong code is a single 401 — no oracle.
    /// </summary>
    [HttpPost("mfa/verify")]
    [EnableRateLimiting(RateLimiting.PasswordlessVerifyPolicy)]
    public async Task<IActionResult> MfaVerify([FromBody] MfaVerifyRequest req, CancellationToken cancellationToken)
    {
        var outcome = await mfaLogin.VerifyChallengeAsync(req.Challenge ?? "", req.Code ?? "", ClientIp, cancellationToken);
        if (outcome is null)
            return Unauthorized(new ErrorResponse("mfa_failed", "Invalid or expired challenge or code."));

        if (!outcome.Native)
            cookieService.SetRefreshTokenCookie(Response, outcome.Session.RefreshToken, Request);
        return Ok(outcome.Session.Response);
    }
}
