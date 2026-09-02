using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Services;

/// <summary>Outcome of an account-erasure attempt (GDPR-2, ADR-011).</summary>
public enum EraseAccountResult
{
    /// <summary>The account (and, for a solo owner, the tenant) was erased.</summary>
    Erased,
    /// <summary>The caller owns a tenant with other members — transfer ownership first.</summary>
    MustTransferFirst,
    /// <summary>Solo owner: erasing dissolves the tenant + wipes its data; needs explicit confirmation.</summary>
    DissolveConfirmationRequired,
    /// <summary>No such user.</summary>
    NoAccount,
}

/// <summary>
/// "Delete my account" (GDPR-2, ADR-011). Removes the caller's identity/PII
/// (<see cref="User"/>/<see cref="UserLogin"/>/<see cref="LoginToken"/>/<see cref="RefreshToken"/>) in
/// one audited transaction, honoring the single-owner invariant (ADR-003): a sole owner with other
/// members must transfer first; a solo owner's tenant is dissolved (its data wiped via the
/// contributors); a plain member is removed — <b>not</b> re-homed, unlike leave, since the account is
/// going away. Tenant app data stays with the tenant; the audit trail survives (actor ids, never PII).
/// </summary>
public interface IAccountErasureService
{
    Task<EraseAccountResult> EraseAsync(Guid userId, bool confirmDissolve, CancellationToken cancellationToken = default);
}

public sealed class AccountErasureService(
    ITenantRepository tenants,
    IUnitOfWork unitOfWork,
    ITenantDissolutionService dissolution,
    IEnumerable<IUserDataContributor> userDataContributors,
    IAuditLog audit,
    IRepository<User> users,
    IRepository<UserLogin> logins,
    IRepository<RefreshToken> refreshTokens,
    IRepository<LoginToken> loginTokens) : IAccountErasureService
{
    public async Task<EraseAccountResult> EraseAsync(Guid userId, bool confirmDissolve, CancellationToken cancellationToken = default)
    {
        var user = await users.Query().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
            return EraseAccountResult.NoAccount;

        var membership = await tenants.GetMembershipAsync(userId, cancellationToken);
        var isOwner = membership is not null
            && string.Equals(membership.Role, TenantRoles.Owner, StringComparison.OrdinalIgnoreCase);
        var memberCount = membership is null
            ? 0
            : (await tenants.GetMembersAsync(membership.TenantId, cancellationToken)).Count;

        // Never strand a tenant ownerless — transfer first.
        if (isOwner && memberCount > 1)
            return EraseAccountResult.MustTransferFirst;

        // A solo owner's erasure dissolves the tenant (destructive) — require confirmation.
        var soloOwner = isOwner && memberCount == 1;
        if (soloOwner && !confirmDissolve)
            return EraseAccountResult.DissolveConfirmationRequired;

        await using var scope = await unitOfWork.BeginTransactionAsync(cancellationToken);

        if (soloOwner && membership is not null)
        {
            // Dissolve: each feature wipes its domain data first, then the core teardown (DEBT-7 —
            // shared with sole-owner leave). The audit trail for this tenant goes with it
            // (AuditDataContributor) — no surviving place to record. Runs inside this transaction.
            await dissolution.DissolveAsync(membership.TenantId, cancellationToken);
        }
        else if (membership is not null)
        {
            // Member/admin: record in the surviving tenant (actor id, no PII), then drop the membership
            // WITHOUT re-homing. RemoveMemberAsync saves, flushing the staged audit event with it.
            await audit.RecordAsync("account.erased", userId, nameof(User), userId.ToString(), null, cancellationToken);
            await tenants.RemoveMemberAsync(membership, cancellationToken);
        }

        // Per-user PII owned by other platform concerns (MFA, notifications, …) is wiped via contributors,
        // so a new user-keyed table is erased without editing this service (v2 audit SOLID-1). These run
        // before the user row (they reference it) and enlist in the ambient transaction.
        foreach (var contributor in userDataContributors)
            await contributor.WipeAsync(userId, cancellationToken);

        // Identity-core PII. LoginTokens are email-keyed; the rest are user-keyed. Children before the
        // user row. Set-based deletes enlist in the ambient transaction.
        await refreshTokens.Query().Where(r => r.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await logins.Query().Where(l => l.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await loginTokens.Query().Where(l => l.Email == user.Email).ExecuteDeleteAsync(cancellationToken);
        await users.Query().Where(u => u.Id == userId).ExecuteDeleteAsync(cancellationToken);

        await scope.CommitAsync(cancellationToken);
        return EraseAccountResult.Erased;
    }
}
