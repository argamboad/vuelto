using Perezosoft.Core.Entities;

namespace Perezosoft.Core.Authorization;

/// <summary>
/// The single source of truth mapping a tenant role to the set of <see cref="Permission"/>s it grants
/// (ADR-009). Roles are ordered <c>owner</c> &gt; <c>admin</c> &gt; <c>member</c>: owner gets every
/// permission, admin gets the management subset, member can only view. Billing and role/ownership
/// changes are owner-only by design (financial / privilege-escalation surface). Unknown roles grant
/// nothing (fail closed). Change authorization by editing this matrix — never by adding a
/// <c>role == "admin"</c> check elsewhere.
/// </summary>
public static class RolePermissions
{
    private static readonly IReadOnlySet<Permission> None = new HashSet<Permission>();

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<Permission>> ByRole =
        new Dictionary<string, IReadOnlySet<Permission>>(StringComparer.OrdinalIgnoreCase)
        {
            [TenantRoles.Owner] = Enum.GetValues<Permission>().ToHashSet(),
            [TenantRoles.Admin] = new HashSet<Permission>
            {
                Permission.ViewTenant,
                Permission.RenameTenant,
                Permission.ManageMembers,
            },
            [TenantRoles.Member] = new HashSet<Permission>
            {
                Permission.ViewTenant,
            },
        };

    /// <summary>The permissions granted to <paramref name="role"/>; empty for null/unknown roles.</summary>
    public static IReadOnlySet<Permission> PermissionsFor(string? role) =>
        role is not null && ByRole.TryGetValue(role, out var permissions) ? permissions : None;

    /// <summary>True if <paramref name="role"/> grants <paramref name="permission"/>.</summary>
    public static bool Grants(string? role, Permission permission) =>
        PermissionsFor(role).Contains(permission);
}
