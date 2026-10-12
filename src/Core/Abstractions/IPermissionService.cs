using Vuelto.Core.Authorization;

namespace Vuelto.Core.Abstractions;

/// <summary>
/// Answers "does the current caller's tenant role grant this <see cref="Permission"/>?" for the
/// authenticated request (ADR-009). The implementation resolves the caller's membership and checks
/// the <see cref="RolePermissions"/> matrix; it <b>fails closed</b> (no principal / no membership →
/// false). This is the seam the minimal-API <c>.RequirePermission(...)</c> filter uses; controllers
/// that already hold the membership check the matrix directly via the base-class helper.
/// </summary>
public interface IPermissionService
{
    Task<bool> HasAsync(Permission permission, CancellationToken cancellationToken = default);
}
