using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Perezosoft.Api.Authentication;
using Perezosoft.Api.Configuration;
using Perezosoft.Api.Services;
using Perezosoft.Core.Authorization;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Perezosoft.Api.Controllers;

/// <summary>
/// Base for JWT-authenticated, tenant-scoped controllers. Centralizes the
/// caller-identity and membership/role helpers every tenant feature needs, so a
/// feature slice doesn't re-copy them. The <c>[Authorize]</c> here is inherited by
/// derived controllers.
/// </summary>
[Authorize(AuthPolicies.TenantApi)]
public abstract class TenantApiControllerBase(ITenantRepository tenants) : ControllerBase
{
    /// <summary>Tenant/membership repository, for derived controllers that read tenant data.</summary>
    protected ITenantRepository Tenants { get; } = tenants;

    /// <summary>The authenticated caller's user id, or null when the token lacks/can't parse it.</summary>
    protected Guid? CurrentUserId => User.GetUserId();

    /// <summary>The caller's single tenant membership (tenant + role), or null.</summary>
    protected Task<TenantMembership?> GetMembershipAsync(CancellationToken cancellationToken = default) =>
        CurrentUserId is { } uid
            ? Tenants.GetMembershipAsync(uid, cancellationToken)
            : Task.FromResult<TenantMembership?>(null);

    /// <summary>True if the caller's role grants <paramref name="permission"/> (ADR-009 matrix).</summary>
    protected static bool HasPermission(TenantMembership membership, Permission permission) =>
        RolePermissions.Grants(membership.Role, permission);

    /// <summary>401 with the standard envelope — the caller's token is missing/invalid.</summary>
    protected IActionResult InvalidToken() =>
        Unauthorized(new ErrorResponse("invalid_token", "Invalid user identity"));

    /// <summary>403 with the standard envelope.</summary>
    protected IActionResult Forbid403(string message) =>
        StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse("forbidden", message));
}
