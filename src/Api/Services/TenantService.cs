using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Perezosoft.Api.Services;

public enum RemoveMemberResult { Removed, NotAMember, CannotRemoveOwner }
public enum TransferResult { Transferred, TargetNotMember, ConcurrentModification }

/// <summary>Outcome of a member role change (RBAC-2, ADR-009).</summary>
public enum ChangeRoleResult
{
    /// <summary>The role was changed (and audited).</summary>
    Changed,
    /// <summary>The target already had that role — no-op, not audited.</summary>
    Unchanged,
    /// <summary>The target user is not a member of this tenant.</summary>
    TargetNotMember,
    /// <summary>The requested role is not assignable here (only <c>admin</c>/<c>member</c>).</summary>
    InvalidRole,
    /// <summary>The owner's role can't be changed here — ownership moves via transfer.</summary>
    CannotChangeOwner,
    /// <summary>A caller can't change their own role.</summary>
    CannotChangeSelf,
}

/// <summary>Outcome of a leave attempt.</summary>
public enum LeaveOutcome
{
    /// <summary>A non-owner member left; old tenant + data persist.</summary>
    Left,
    /// <summary>Owner tried to leave while members remain.</summary>
    MustTransferFirst,
    /// <summary>Sole owner leaving needs an explicit confirm to wipe.</summary>
    ConfirmationRequired,
    /// <summary>Sole owner left; tenant + data wiped.</summary>
    Dissolved
}

/// <summary>
/// Tenant management writes: rename, remove-member, ownership-transfer, and leave.
/// Role enforcement lives at the controller (the role matrix). Re-home semantics
/// keep every user in exactly one tenant.
/// </summary>
public interface ITenantService
{
    /// <summary>Renames the tenant. False when it doesn't exist.</summary>
    Task<bool> RenameAsync(Guid tenantId, string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a member from the tenant and lands them in a fresh tenant-of-one
    /// (owner) so they are never tenant-less. Their contributed data stays with
    /// the tenant. The owner is never removable here (single-owner invariant) —
    /// they leave via transfer/dissolve.
    /// </summary>
    Task<RemoveMemberResult> RemoveMemberAsync(Guid tenantId, Guid targetUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes a member's role between <c>admin</c> and <c>member</c> (RBAC-2, ADR-009). The owner role
    /// is never set or cleared here (ownership moves via <see cref="TransferOwnershipAsync"/>), the owner
    /// is never targeted, and a caller can't change their own role. A real change is recorded in the audit
    /// log atomically with the update; setting the role the member already has is an idempotent no-op.
    /// </summary>
    Task<ChangeRoleResult> ChangeMemberRoleAsync(Guid tenantId, Guid actorUserId, Guid targetUserId, string newRole, CancellationToken cancellationToken = default);

    /// <summary>
    /// Transfers ownership to an existing member — the target becomes owner, the
    /// caller becomes member, in one transaction (single-owner invariant).
    /// </summary>
    Task<TransferResult> TransferOwnershipAsync(Guid tenantId, Guid currentOwnerUserId, Guid targetUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Leaves the caller's tenant. A non-owner member leaves (data stays); an owner
    /// with members remaining is refused (<see cref="LeaveOutcome.MustTransferFirst"/>);
    /// the sole member (owner) dissolves the tenant + wipes its data, but only with
    /// <paramref name="confirmDissolve"/> (else <see cref="LeaveOutcome.ConfirmationRequired"/>).
    /// In every non-refused case the user lands in a fresh empty tenant-of-one.
    /// </summary>
    Task<LeaveOutcome> LeaveAsync(Guid userId, bool confirmDissolve, CancellationToken cancellationToken = default);
}

public class TenantService(
    ITenantRepository tenants,
    IUnitOfWork unitOfWork,
    ITenantDissolutionService dissolution,
    TimeProvider clock,
    ILogger<TenantService> logger,
    IAuditLog audit) : ITenantService
{
    // The tenant a re-homed user lands in (UI label is "Household").
    private const string ReHomeTenantName = "My Household";

    public async Task<bool> RenameAsync(Guid tenantId, string name, CancellationToken cancellationToken = default)
    {
        var tenant = await tenants.GetByIdAsync(tenantId, cancellationToken);
        if (tenant == null) return false;
        tenant.Name = name.Trim();
        await tenants.UpdateTenantAsync(tenant, cancellationToken);
        logger.LogInformation("Tenant {TenantId} renamed", tenantId);
        return true;
    }

    public async Task<RemoveMemberResult> RemoveMemberAsync(Guid tenantId, Guid targetUserId, CancellationToken cancellationToken = default)
    {
        var membership = await tenants.GetMembershipAsync(targetUserId, cancellationToken);
        if (membership == null || membership.TenantId != tenantId)
            return RemoveMemberResult.NotAMember;

        // The owner is never removable here — that would orphan the tenant (single-owner invariant).
        // Now that admins hold ManageMembers (RBAC-2), guard this server-side, not just in the UI.
        if (string.Equals(membership.Role, TenantRoles.Owner, StringComparison.OrdinalIgnoreCase))
            return RemoveMemberResult.CannotRemoveOwner;

        // Drop the membership and re-home the user atomically.
        await using var scope = await unitOfWork.BeginTransactionAsync(cancellationToken);

        await tenants.RemoveMemberAsync(membership, cancellationToken);
        await ReHomeAsync(targetUserId, cancellationToken);
        await scope.CommitAsync(cancellationToken);

        logger.LogInformation("Member {UserId} removed from tenant {TenantId}; re-homed",
            targetUserId, tenantId);
        return RemoveMemberResult.Removed;
    }

    public async Task<ChangeRoleResult> ChangeMemberRoleAsync(Guid tenantId, Guid actorUserId, Guid targetUserId, string newRole, CancellationToken cancellationToken = default)
    {
        // Only admin/member are assignable here; owner is conferred via transfer (ADR-009).
        if (!TryNormalizeAssignableRole(newRole, out var normalized))
            return ChangeRoleResult.InvalidRole;
        if (targetUserId == actorUserId)
            return ChangeRoleResult.CannotChangeSelf;

        var membership = await tenants.GetMembershipAsync(targetUserId, cancellationToken);
        if (membership == null || membership.TenantId != tenantId)
            return ChangeRoleResult.TargetNotMember;

        // The owner's role is never changed here — ownership moves via transfer only.
        if (string.Equals(membership.Role, TenantRoles.Owner, StringComparison.OrdinalIgnoreCase))
            return ChangeRoleResult.CannotChangeOwner;

        if (string.Equals(membership.Role, normalized, StringComparison.OrdinalIgnoreCase))
            return ChangeRoleResult.Unchanged; // idempotent no-op — nothing to change or audit

        var oldRole = membership.Role;
        // Stage the audit event first, then persist both in one SaveChanges so the role change and its
        // audit record commit atomically (the audit stages on the shared unit of work — ADR-008).
        await audit.RecordAsync(
            "member.role_changed", actorUserId, nameof(TenantMembership), membership.Id.ToString(),
            new { user_id = targetUserId, from = oldRole, to = normalized }, cancellationToken);
        membership.Role = normalized;
        await tenants.UpdateMemberAsync(membership, cancellationToken);

        logger.LogInformation("Tenant {TenantId} member {UserId} role {From} -> {To} by {Actor}",
            tenantId, targetUserId, oldRole, normalized, actorUserId);
        return ChangeRoleResult.Changed;
    }

    // The roles assignable via this endpoint, normalized to their canonical constant. Owner is
    // intentionally excluded — it is conferred only through TransferOwnershipAsync.
    private static bool TryNormalizeAssignableRole(string role, out string normalized)
    {
        if (string.Equals(role, TenantRoles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            normalized = TenantRoles.Admin;
            return true;
        }
        if (string.Equals(role, TenantRoles.Member, StringComparison.OrdinalIgnoreCase))
        {
            normalized = TenantRoles.Member;
            return true;
        }
        normalized = string.Empty;
        return false;
    }

    public async Task<TransferResult> TransferOwnershipAsync(Guid tenantId, Guid currentOwnerUserId, Guid targetUserId, CancellationToken cancellationToken = default)
    {
        var members = await tenants.GetMembersAsync(tenantId, cancellationToken);
        if (members.All(m => m.UserId != targetUserId)) return TransferResult.TargetNotMember;

        // Conditional update guards the single-owner invariant under concurrency.
        await using var scope = await unitOfWork.BeginTransactionAsync(cancellationToken);

        if (!await tenants.TryTransferOwnershipAsync(tenantId, currentOwnerUserId, targetUserId, cancellationToken))
            return TransferResult.ConcurrentModification;

        await scope.CommitAsync(cancellationToken);

        logger.LogInformation("Tenant {TenantId} ownership transferred {From} -> {To}",
            tenantId, currentOwnerUserId, targetUserId);
        return TransferResult.Transferred;
    }

    public async Task<LeaveOutcome> LeaveAsync(Guid userId, bool confirmDissolve, CancellationToken cancellationToken = default)
    {
        var membership = await tenants.GetMembershipAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("User has no tenant");
        var tenantId = membership.TenantId;
        var members = await tenants.GetMembersAsync(tenantId, cancellationToken);

        // Sole member (necessarily the owner) → dissolution.
        if (members.Count == 1)
        {
            if (!confirmDissolve) return LeaveOutcome.ConfirmationRequired;

            // Wipe + re-home atomically — a partial wipe is impossible. The shared dissolve sequence
            // (DEBT-7) wipes each feature's domain data first, then the platform's core teardown; it
            // runs inside this transaction alongside the re-home.
            await using var scope = await unitOfWork.BeginTransactionAsync(cancellationToken);
            await dissolution.DissolveAsync(tenantId, cancellationToken);
            await ReHomeAsync(userId, cancellationToken);
            await scope.CommitAsync(cancellationToken);

            logger.LogInformation("Tenant {TenantId} dissolved by sole owner {UserId}", tenantId, userId);
            return LeaveOutcome.Dissolved;
        }

        // Owner can't strand a multi-member tenant — transfer first.
        if (string.Equals(membership.Role, TenantRoles.Owner, StringComparison.OrdinalIgnoreCase))
            return LeaveOutcome.MustTransferFirst;

        // Non-owner member leaves; the tenant + its data stay.
        await using (var scope = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            await tenants.RemoveMemberAsync(membership, cancellationToken);
            await ReHomeAsync(userId, cancellationToken);
            await scope.CommitAsync(cancellationToken);
        }

        logger.LogInformation("Member {UserId} left tenant {TenantId}", userId, tenantId);
        return LeaveOutcome.Left;
    }

    // Lands a departing user in a fresh empty tenant-of-one (owner) so they are
    // never tenant-less.
    private async Task ReHomeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var newTenantId = Guid.CreateVersion7();
        await tenants.CreateAsync(new Tenant
        {
            Id = newTenantId,
            Name = ReHomeTenantName,
            CreatedAt = now,
            UpdatedAt = now
        }, cancellationToken);
        await tenants.AddMemberAsync(new TenantMembership
        {
            Id = Guid.CreateVersion7(),
            TenantId = newTenantId,
            UserId = userId,
            Role = TenantRoles.Owner,
            JoinedAt = now
        }, cancellationToken);
    }
}
