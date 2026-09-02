using System.Net.Mail;
using Vuelto.Api.Configuration;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;
using Vuelto.Infrastructure.Email;

namespace Vuelto.Api.Services;

public enum InviteCreateStatus { Created, InvalidEmail, AlreadyMember, SeatLimitReached }

public record InviteCreateResult(InviteCreateStatus Status, TenantInvitation? Invitation = null, string? RawToken = null);

public enum InviteRegenerateStatus { Regenerated, NotFound, NotPending }

public record InviteRegenerateResult(InviteRegenerateStatus Status, TenantInvitation? Invitation = null, string? RawToken = null);

/// <summary>Outcome of accepting an invitation.</summary>
public enum AcceptStatus { Joined, InvalidToken, NoHousehold, AlreadyMember, MustTransferFirst, WouldAbandonData, SeatLimitReached }

/// <summary>
/// Tenant-invitation flow. Uses result objects (not exceptions) for expected
/// validation/flow outcomes — consistent with <see cref="ITenantService"/>; the
/// controller maps each result to an HTTP status.
/// </summary>
public interface ITenantInvitationService
{
    /// <summary>Creates (or refreshes an existing pending) invitation. The caller has
    /// already been verified as the tenant's owner. Emails the invite and returns the
    /// saved invitation plus the raw token (one-time reveal — not stored).</summary>
    Task<InviteCreateResult> CreateAsync(Guid tenantId, Guid inviterUserId, string email, CancellationToken cancellationToken = default);

    Task<List<TenantInvitation>> GetPendingAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Revokes the old hash and issues a fresh token for an existing pending invite.
    /// Emails the new invite and returns the saved invitation plus the new raw token.</summary>
    Task<InviteRegenerateResult> RegenerateAsync(Guid tenantId, Guid invitationId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Revokes a pending invite owned by the tenant. False = not found for this
    /// tenant (controller → 404).</summary>
    Task<bool> RevokeAsync(Guid tenantId, Guid invitationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeems a token for the signed-in user, moving their membership to the inviting tenant.
    /// <para>
    /// By design this is a <b>bearer capability</b> (D4 / LOGIC-S3, "leave bearer"): whoever holds a
    /// valid, unexpired token can accept — acceptance is NOT bound to the invited email address, so the
    /// signed-in user need not match <c>InvitedEmail</c>. This is intentional (email-binding was
    /// deferred); the token is single-use, hashed, and time-limited, which is the security boundary.
    /// Don't "fix" this to require an email match without revisiting D4.
    /// </para>
    /// </summary>
    Task<AcceptStatus> AcceptAsync(Guid userId, string token, CancellationToken cancellationToken = default);
}

public class TenantInvitationService(
    ITenantInvitationRepository invitations,
    ITenantRepository tenants,
    ITokenGenerator tokenGenerator,
    ITokenHasher tokenHasher,
    IUnitOfWork unitOfWork,
    IEmailSender emailSender,
    IUserService userService,
    IApplicationSettings appSettings,
    IInvitationSettings invitationSettings,
    IEnumerable<ITenantDataContributor> dataContributors,
    IQuotaService quota,
    ITenantContext tenantContext,
    TimeProvider clock,
    ILogger<TenantInvitationService> logger) : ITenantInvitationService
{
    private TimeSpan InvitationTtl => TimeSpan.FromDays(invitationSettings.LifespanDays);

    public async Task<InviteCreateResult> CreateAsync(Guid tenantId, Guid inviterUserId, string email, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email) || !MailAddress.TryCreate(email.Trim(), out _))
            return new InviteCreateResult(InviteCreateStatus.InvalidEmail);

        var normalized = email.Trim().ToLowerInvariant();

        // Can't invite someone who's already a member of this tenant.
        if (await tenants.IsEmailMemberAsync(tenantId, normalized, cancellationToken))
            return new InviteCreateResult(InviteCreateStatus.AlreadyMember);

        var now = clock.GetUtcNow();
        var rawToken = tokenGenerator.GenerateToken();
        var tokenHash = tokenHasher.HashToken(rawToken);

        // A pending invite for the same (tenant, email) is refreshed, not duplicated.
        var existing = await invitations.GetPendingByEmailAsync(tenantId, normalized, cancellationToken);
        TenantInvitation invitation;
        if (existing != null)
        {
            existing.TokenHash = tokenHash;
            existing.InvitedByUserId = inviterUserId;
            existing.CreatedAt = now;
            existing.ExpiresAt = now + InvitationTtl;
            await invitations.UpdateAsync(existing, cancellationToken);
            logger.LogInformation("Refreshed pending invitation {Id} for tenant {TenantId}", existing.Id, tenantId);
            invitation = existing;
        }
        else
        {
            // A brand-new invite claims a seat (members + pending) — enforce the plan's seat quota
            // (BILLING-5). Refreshing an existing pending invite (above) reuses its seat, so it's exempt.
            if (!await quota.CanAddSeatsAsync(1, cancellationToken))
            {
                logger.LogInformation("Invite to tenant {TenantId} blocked: seat limit reached", tenantId);
                return new InviteCreateResult(InviteCreateStatus.SeatLimitReached);
            }

            invitation = await invitations.CreateAsync(new TenantInvitation
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                InvitedEmail = normalized,
                InvitedByUserId = inviterUserId,
                Status = InvitationStatuses.Pending,
                TokenHash = tokenHash,
                CreatedAt = now,
                ExpiresAt = now + InvitationTtl
            }, cancellationToken);
            logger.LogInformation("Created invitation {Id} for tenant {TenantId}", invitation.Id, tenantId);
        }

        await SendInvitationEmailAsync(normalized, rawToken, inviterUserId, cancellationToken);
        return new InviteCreateResult(InviteCreateStatus.Created, invitation, rawToken);
    }

    public async Task<InviteRegenerateResult> RegenerateAsync(Guid tenantId, Guid invitationId, Guid userId, CancellationToken cancellationToken = default)
    {
        var invitation = await invitations.GetByIdUnscopedAsync(invitationId, cancellationToken);
        // Cross-tenant treated as not-found — no existence oracle on opaque IDs.
        if (invitation == null || invitation.TenantId != tenantId)
            return new InviteRegenerateResult(InviteRegenerateStatus.NotFound);
        if (invitation.Status != InvitationStatuses.Pending)
            return new InviteRegenerateResult(InviteRegenerateStatus.NotPending);

        var now = clock.GetUtcNow();
        var rawToken = tokenGenerator.GenerateToken();
        invitation.TokenHash = tokenHasher.HashToken(rawToken);
        invitation.InvitedByUserId = userId;
        invitation.CreatedAt = now;
        invitation.ExpiresAt = now + InvitationTtl;
        await invitations.UpdateAsync(invitation, cancellationToken);
        logger.LogInformation("Regenerated token for invitation {Id} (tenant {TenantId})", invitation.Id, tenantId);

        await SendInvitationEmailAsync(invitation.InvitedEmail, rawToken, userId, cancellationToken);
        return new InviteRegenerateResult(InviteRegenerateStatus.Regenerated, invitation, rawToken);
    }

    public Task<List<TenantInvitation>> GetPendingAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        invitations.GetPendingForTenantAsync(tenantId, cancellationToken);

    public async Task<bool> RevokeAsync(Guid tenantId, Guid invitationId, CancellationToken cancellationToken = default)
    {
        var invitation = await invitations.GetByIdUnscopedAsync(invitationId, cancellationToken);
        // Cross-tenant treated as not-found — no existence oracle on opaque IDs.
        if (invitation == null || invitation.TenantId != tenantId) return false;

        if (invitation.Status == InvitationStatuses.Pending)
        {
            invitation.Status = InvitationStatuses.Revoked;
            await invitations.UpdateAsync(invitation, cancellationToken);
            logger.LogInformation("Revoked invitation {Id}", invitationId);
        }
        return true;
    }

    public async Task<AcceptStatus> AcceptAsync(Guid userId, string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            return AcceptStatus.InvalidToken;

        // Hash the presented token before lookup; never compare raw values.
        var tokenHash = tokenHasher.HashToken(token);
        var invitation = await invitations.GetByTokenHashAsync(tokenHash, cancellationToken);
        var now = clock.GetUtcNow();

        // Unknown / revoked / accepted / past-expiry → invalid (don't leak which).
        if (invitation == null
            || invitation.Status != InvitationStatuses.Pending
            || invitation.ExpiresAt <= now)
            return AcceptStatus.InvalidToken;

        var membership = await tenants.GetMembershipAsync(userId, cancellationToken);
        if (membership is null)
            return AcceptStatus.NoHousehold;

        if (membership.TenantId == invitation.TenantId)
            return AcceptStatus.AlreadyMember;

        var oldTenantId = membership.TenantId;
        var members = await tenants.GetMembersAsync(oldTenantId, cancellationToken);
        var isOwner = string.Equals(membership.Role, TenantRoles.Owner, StringComparison.OrdinalIgnoreCase);
        var soloOwner = isOwner && members.Count == 1;

        // An owner of a multi-member tenant must hand off first.
        if (isOwner && members.Count > 1)
            return AcceptStatus.MustTransferFirst;

        // A solo owner carrying real data can't silently abandon it.
        var dissolveOld = false;
        if (soloOwner)
        {
            if (await TenantHasDataAsync(oldTenantId, cancellationToken))
                return AcceptStatus.WouldAbandonData;
            dissolveOld = true; // empty solo tenant-of-one is dissolved on join
        }

        // Seat re-check (BILLING-9, ADR-006 addendum): a downgrade (dunning lapse, cancel, admin
        // comp revert) can leave more reserved seats — members + pending invites — than the new
        // plan allows, and nothing sweeps the invites. The accept itself is seat-neutral (the
        // joiner consumes the seat their pending invite reserved), so the rule is "already over
        // the limit" (CanAdd(0)), NOT "can add one more" — accepts at exactly the cap stay
        // allowed, and a refused token stays pending and self-heals when the tenant upgrades.
        // EnterTenant: the caller's JWT still carries their old tenant; the quota must count the
        // INVITATION's tenant (the token was verified above — the same trusted-scoping contract
        // as the conditional flip below).
        using (tenantContext.EnterTenant(invitation.TenantId))
        {
            if (!await quota.CanAddSeatsAsync(0, cancellationToken))
                return AcceptStatus.SeatLimitReached;
        }

        // Move membership + consume token + dissolve old solo tenant atomically.
        await using var scope = await unitOfWork.BeginTransactionAsync(cancellationToken);

        membership.TenantId = invitation.TenantId;
        membership.Role = TenantRoles.Member;
        membership.JoinedAt = now;
        await tenants.UpdateMemberAsync(membership, cancellationToken);

        // Conditional flip — only one concurrent accept can update the row. If another
        // accept already won the race, the scope disposes without CommitAsync and the
        // membership move rolls back.
        //
        // EnterTenant: the flip updates the INVITATION's tenant's row while the caller's JWT still
        // carries their old tenant. The RLS backstop (ADR-020) scopes set-based writes to the
        // current tenant (query tags don't render in the ExecuteUpdate pipeline), so the accept
        // enters the invitation's tenant for this one write — the token was verified above, which
        // is exactly the trusted-scoping contract of ITenantContext (same as the billing webhook).
        using (tenantContext.EnterTenant(invitation.TenantId))
        {
            if (!await invitations.TryAcceptAsync(invitation.Id, cancellationToken))
                return AcceptStatus.InvalidToken;
        }

        if (dissolveOld)
            await tenants.DeleteTenantAsync(oldTenantId, cancellationToken);

        await scope.CommitAsync(cancellationToken);

        logger.LogInformation("User {UserId} accepted invitation {Id} -> tenant {TenantId}",
            userId, invitation.Id, invitation.TenantId);
        return AcceptStatus.Joined;
    }

    // Any feature contributor holding data for the tenant blocks a silent abandon.
    private async Task<bool> TenantHasDataAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        foreach (var contributor in dataContributors)
            if (await contributor.HasDataAsync(tenantId, cancellationToken))
                return true;
        return false;
    }

    private async Task SendInvitationEmailAsync(string email, string rawToken, Guid inviterUserId, CancellationToken cancellationToken = default)
    {
        var joinUrl = $"{appSettings.ClientUrl}/join?token={Uri.EscapeDataString(rawToken)}";
        try
        {
            // Invites go out in the inviter's saved language (the recipient may have no account).
            var inviter = await userService.GetUserByIdAsync(inviterUserId, cancellationToken);
            var emailBody = BrandedEmail.Invitation(joinUrl, rawToken, BrandedEmail.ResolveCulture(inviter?.Locale));
            await emailSender.SendAsync(email, emailBody.Subject, emailBody.Html, emailBody.InlineImages, cancellationToken);
        }
        catch (Exception ex)
        {
            // Delivery is best-effort — the owner can still share the raw token returned
            // in the API response.
            logger.LogWarning(ex, "Failed to send invitation email to {Email}", email);
        }
    }
}
