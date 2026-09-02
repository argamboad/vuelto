using Perezosoft.Core.Authorization;
using Perezosoft.Core.Entities;

namespace Perezosoft.Core.Tests;

/// <summary>
/// RBAC-1 (ADR-009): the <see cref="RolePermissions"/> matrix is the single source of truth mapping a
/// tenant role to the capabilities it grants. owner > admin > member; billing and role/ownership
/// changes are owner-only by design; unknown roles grant nothing (fail closed).
/// </summary>
public class RolePermissionsTests
{
    [Theory]
    [InlineData(Permission.ViewTenant)]
    [InlineData(Permission.RenameTenant)]
    [InlineData(Permission.ManageMembers)]
    [InlineData(Permission.ManageRoles)]
    [InlineData(Permission.ManageBilling)]
    [InlineData(Permission.ExportData)]
    [InlineData(Permission.TransferOwnership)]
    [InlineData(Permission.DissolveTenant)]
    public void Owner_HasEveryPermission(Permission permission) =>
        Assert.True(RolePermissions.Grants(TenantRoles.Owner, permission));

    [Theory]
    [InlineData(Permission.ViewTenant, true)]
    [InlineData(Permission.RenameTenant, true)]
    [InlineData(Permission.ManageMembers, true)]
    [InlineData(Permission.ManageRoles, false)]
    [InlineData(Permission.ManageBilling, false)]
    [InlineData(Permission.ExportData, false)]
    [InlineData(Permission.TransferOwnership, false)]
    [InlineData(Permission.DissolveTenant, false)]
    public void Admin_HasManagementButNotOwnerOnlyPermissions(Permission permission, bool granted) =>
        Assert.Equal(granted, RolePermissions.Grants(TenantRoles.Admin, permission));

    [Theory]
    [InlineData(Permission.ViewTenant, true)]
    [InlineData(Permission.RenameTenant, false)]
    [InlineData(Permission.ManageMembers, false)]
    [InlineData(Permission.ManageRoles, false)]
    [InlineData(Permission.ManageBilling, false)]
    [InlineData(Permission.ExportData, false)]
    [InlineData(Permission.TransferOwnership, false)]
    [InlineData(Permission.DissolveTenant, false)]
    public void Member_HasOnlyViewTenant(Permission permission, bool granted) =>
        Assert.Equal(granted, RolePermissions.Grants(TenantRoles.Member, permission));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("superadmin")]
    public void UnknownRole_GrantsNothing(string? role)
    {
        foreach (var permission in Enum.GetValues<Permission>())
            Assert.False(RolePermissions.Grants(role, permission));
    }

    [Theory]
    [InlineData("OWNER")]
    [InlineData("Admin")]
    [InlineData("Member")]
    public void RoleMatching_IsCaseInsensitive(string role) =>
        Assert.True(RolePermissions.Grants(role, Permission.ViewTenant));

    [Fact]
    public void Ordering_OwnerSupersetOfAdmin_SupersetOfMember()
    {
        var owner = RolePermissions.PermissionsFor(TenantRoles.Owner);
        var admin = RolePermissions.PermissionsFor(TenantRoles.Admin);
        var member = RolePermissions.PermissionsFor(TenantRoles.Member);

        Assert.True(admin.IsProperSubsetOf(owner));
        Assert.True(member.IsProperSubsetOf(admin));
    }
}
