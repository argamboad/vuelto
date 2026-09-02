using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Perezosoft.Api.Authentication;
using Perezosoft.Api.Services;

namespace Perezosoft.Api.Controllers;

/// <summary>
/// Base for the platform-staff admin surface (ADR-014). Authenticated as a normal user (the app JWT),
/// then gated per-request by the staff allowlist — there is no separate admin login. Derived controllers
/// call <see cref="RequireStaffAsync"/> first; a non-staff caller gets <b>403</b>. Staff membership is
/// out-of-band config, never a tenant role.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public abstract class AdminApiControllerBase(IPlatformStaffService staff) : ControllerBase
{
    protected Guid? CurrentUserId => User.GetUserId();

    /// <summary>
    /// Non-gating staff check: true when the current caller is platform staff. Unlike
    /// <see cref="RequireStaffAsync"/> this never produces a 403 — for the status probe the client uses to
    /// decide whether to show the admin UI. False under an impersonation token, matching the gate (and the
    /// client, which already hides the admin UI while impersonating).
    /// </summary>
    protected async Task<bool> IsCurrentUserStaffAsync(CancellationToken cancellationToken)
        => !User.IsImpersonation()
           && CurrentUserId is { } userId && await staff.IsStaffAsync(userId, cancellationToken);

    /// <summary>
    /// Gate for admin actions: returns the staff user id when the caller is platform staff, otherwise a
    /// ready-to-return 401 (no identity) / 403 (not staff). <b>Impersonation tokens are rejected outright</b>
    /// (v3 audit ADM-2): staff powers never transit a "sign in as" session — otherwise staff A impersonating
    /// staff B could act on this surface with every audit row attributing B.
    /// </summary>
    protected async Task<(Guid StaffUserId, IActionResult? Denied)> RequireStaffAsync(CancellationToken cancellationToken)
    {
        if (CurrentUserId is not { } userId)
            return (default, Unauthorized(new ErrorResponse("invalid_token", "Invalid user identity")));

        if (User.IsImpersonation())
            return (default, StatusCode(StatusCodes.Status403Forbidden,
                new ErrorResponse("impersonation_not_allowed", "Staff actions are not available from an impersonation session")));

        if (!await staff.IsStaffAsync(userId, cancellationToken))
            return (default, StatusCode(StatusCodes.Status403Forbidden,
                new ErrorResponse("forbidden", "Platform-staff only")));

        return (userId, null);
    }
}
