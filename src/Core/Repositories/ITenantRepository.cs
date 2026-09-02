using Perezosoft.Core.Entities;

namespace Perezosoft.Core.Repositories;

/// <summary>
/// Tenants and their membership. Identity (logins, inboxes) stays user-scoped;
/// this repository owns the user→tenant resolution that app-data scoping depends
/// on.
/// </summary>
public interface ITenantRepository
{
    /// <summary>
    /// Resolves the caller's tenant id via membership; null when the user has no
    /// membership (callers fail closed — never an unscoped read).
    /// </summary>
    Task<Guid?> GetTenantIdForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<Tenant> CreateAsync(Tenant tenant, CancellationToken cancellationToken = default);
    Task<TenantMembership> AddMemberAsync(TenantMembership member, CancellationToken cancellationToken = default);

    Task<Tenant?> GetByIdAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>The user's single membership (tenant + role), or null.</summary>
    /// <summary>
    /// The user's membership. DETERMINISTIC (oldest first) so a user who somehow holds more than one never
    /// resolves against an arbitrary row (v3 audit LB-ADM-3). For an AUTHZ decision use the tenant-scoped
    /// overload instead — the permission gate must resolve the membership for the caller's JWT tenant.
    /// </summary>
    Task<TenantMembership?> GetMembershipAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The user's membership IN a specific tenant (the (user, tenant) pair is unique) — the deterministic
    /// lookup authz uses so a permission decision is always keyed on the caller's JWT <c>tenant_id</c>,
    /// never an arbitrary membership (v3 audit LB-ADM-3). Null when the user is not a member of that tenant.
    /// </summary>
    Task<TenantMembership?> GetMembershipAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken = default);

    Task<List<TenantMembership>> GetMembersAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>True if any member of the tenant has the given (normalized) email.</summary>
    Task<bool> IsEmailMemberAsync(Guid tenantId, string email, CancellationToken cancellationToken = default);

    Task UpdateMemberAsync(TenantMembership member, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically flips <paramref name="currentOwnerUserId"/> from 'owner' to 'member'
    /// and <paramref name="targetUserId"/> to 'owner', guarded by a conditional update
    /// on the current owner's role. Returns false if the current owner no longer holds
    /// the 'owner' role (race lost — caller must abort its transaction scope).
    /// Must be called inside an active <see cref="IUnitOfWork"/> transaction scope.
    /// </summary>
    Task<bool> TryTransferOwnershipAsync(Guid tenantId, Guid currentOwnerUserId, Guid targetUserId, CancellationToken cancellationToken = default);

    /// <summary>Hard-deletes a tenant (cascades its data) — used when a solo
    /// tenant-of-one is dissolved as its owner joins another.</summary>
    Task DeleteTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>The tenant's members joined to their user display info.</summary>
    Task<List<TenantMemberDetail>> GetMemberDetailsAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every tenant with its member count — for the platform-staff admin surface (ADR-014). Tenants and
    /// memberships are not tenant-scoped, so this is a plain cross-tenant read (no filter to bypass).
    /// </summary>
    Task<List<TenantSummary>> ListAllAsync(CancellationToken cancellationToken = default);

    Task UpdateTenantAsync(Tenant tenant, CancellationToken cancellationToken = default);

    Task RemoveMemberAsync(TenantMembership member, CancellationToken cancellationToken = default);

    /// <summary>
    /// Core tenant teardown in one transaction: removes invitations, memberships, and the
    /// tenant row. Feature/domain data is wiped separately by each
    /// <see cref="Perezosoft.Core.Abstractions.ITenantDataContributor"/>; user-scoped data
    /// (logins, tokens) is untouched.
    /// </summary>
    Task WipeDataAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>A roster row: membership joined to the user's display info.</summary>
public record TenantMemberDetail(
    Guid UserId, string? DisplayName, string Email, string Role, DateTimeOffset JoinedAt);

/// <summary>A tenant summary for the admin tenant list.</summary>
public record TenantSummary(Guid Id, string Name, DateTimeOffset CreatedAt, int MemberCount);
