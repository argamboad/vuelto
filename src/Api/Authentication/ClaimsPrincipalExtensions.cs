using System.Security.Claims;

namespace Vuelto.Api.Authentication;

/// <summary>
/// Shared accessors for the authenticated principal, so the "current user id from the NameIdentifier
/// claim" parse lives in one tested place instead of being re-implemented in every controller and
/// endpoint (v2 audit DEBT-2).
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>The authenticated user id from the <see cref="ClaimTypes.NameIdentifier"/> claim, or null if absent/unparseable.</summary>
    public static Guid? GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>The authenticated caller's tenant from the <c>tenant_id</c> claim, or null if absent/unparseable.
    /// Authz resolves membership for THIS tenant so a permission decision is keyed on the JWT, not an
    /// arbitrary membership (v3 audit LB-ADM-3).</summary>
    public static Guid? GetTenantId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(Services.JwtClaims.TenantId), out var id) ? id : null;

    /// <summary>
    /// The staff user driving this request via an impersonation token (the <c>impersonated_by</c> claim,
    /// ADMIN-2/ADR-014), or null on a normal token. Fail-closed on an unparseable value: a present-but-
    /// mangled claim still reads as "impersonating" for gating purposes via <see cref="IsImpersonation"/>.
    /// </summary>
    public static Guid? GetImpersonatedBy(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(Services.JwtClaims.ImpersonatedBy), out var id) ? id : null;

    /// <summary>True when the principal carries the <c>impersonated_by</c> claim at all — the gate check
    /// for surfaces impersonation must never reach (staff endpoints, account-preference writes).</summary>
    public static bool IsImpersonation(this ClaimsPrincipal principal) =>
        principal.FindFirst(Services.JwtClaims.ImpersonatedBy) is not null;
}
