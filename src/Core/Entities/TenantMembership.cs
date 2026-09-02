namespace Perezosoft.Core.Entities;

/// <summary>
/// Links a user to their tenant with a role. A user belongs to exactly one
/// tenant at a time (unique on <see cref="UserId"/>); a tenant has exactly one
/// <c>owner</c>. Identity (logins, inbox) stays user-scoped; app data is scoped
/// to the tenant. Membership — not <c>User.TenantId</c> — is the source of truth
/// for tenant resolution.
/// </summary>
public class TenantMembership
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }

    /// <summary><see cref="TenantRoles.Owner"/> | <see cref="TenantRoles.Admin"/> | <see cref="TenantRoles.Member"/>.</summary>
    public string Role { get; set; } = TenantRoles.Member;

    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Role constants for <see cref="TenantMembership.Role"/>, ordered <c>owner</c> &gt; <c>admin</c> &gt;
/// <c>member</c> (ADR-009). A tenant has exactly one <c>owner</c>; <c>admin</c> is a delegated-management
/// tier. What each role can do is defined once in <c>RolePermissions</c> — not by ad-hoc role checks.
/// </summary>
public static class TenantRoles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Member = "member";
}
