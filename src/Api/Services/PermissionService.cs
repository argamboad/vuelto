using Microsoft.AspNetCore.Http;
using Vuelto.Api.Authentication;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Authorization;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Services;

/// <summary>
/// Resolves the authenticated caller's tenant membership (by the <see cref="ClaimTypes.NameIdentifier"/>
/// claim) and answers permission checks via the <see cref="RolePermissions"/> matrix (ADR-009).
/// <b>Fails closed</b>: no HTTP context, no authenticated user, or no membership all yield
/// <c>false</c>. Registered request-scoped (mirrors <see cref="EntitlementService"/>).
/// </summary>
public sealed class PermissionService(IHttpContextAccessor accessor, ITenantRepository tenants) : IPermissionService
{
    public async Task<bool> HasAsync(Permission permission, CancellationToken cancellationToken = default)
    {
        var principal = accessor.HttpContext?.User;
        // Resolve the membership for the caller's JWT tenant, not an arbitrary one (LB-ADM-3). Missing user
        // or tenant claim ⇒ fail closed.
        if (principal?.GetUserId() is not { } userId || principal.GetTenantId() is not { } tenantId)
            return false;

        var membership = await tenants.GetMembershipAsync(userId, tenantId, cancellationToken);
        return membership is not null && RolePermissions.Grants(membership.Role, permission);
    }
}
