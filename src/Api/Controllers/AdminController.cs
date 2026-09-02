using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Models;
using Vuelto.Api.Services;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Billing;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Controllers;

/// <summary>
/// Platform-staff back-office (ADMIN-1, ADR-014): read-only cross-tenant inspection. The global tenant
/// filter is never loosened — the tenant list reads non-tenant-scoped tables directly, and the per-tenant
/// detail <b>enters the target tenant</b> (<see cref="ITenantContext.EnterTenant"/>, ADR-003) so its
/// tenant-scoped data is read through the normal filter, scoped to that tenant. Viewing a tenant is
/// <b>audited in that tenant</b>, so its owner can see a platform admin looked.
/// </summary>
[Route("api/admin")]
public class AdminController(
    IPlatformStaffService staff,
    ITenantRepository tenants,
    ITenantContext tenantContext,
    IAuditLog audit,
    IRepository<Subscription> subscriptions,
    IRepository<AuditEvent> auditEvents,
    IUserRepository users,
    IJwtTokenService jwt,
    INotificationService notifications,
    IMfaService mfaService,
    IRefreshTokenService refreshTokens,
    IOutbox outbox,
    IUnitOfWork unitOfWork,
    ILogger<AdminController> logger,
    TimeProvider clock) : AdminApiControllerBase(staff)
{
    // Impersonation tokens are deliberately short-lived and non-refreshable (ADR-014).
    private static readonly TimeSpan ImpersonationLifetime = TimeSpan.FromMinutes(15);

    private const int AnnounceTitleMaxLength = 200;
    private const int AnnounceBodyMaxLength = 2000;

    /// <summary>
    /// Whether the authenticated caller is platform staff. Unlike the other actions this never 403s —
    /// any signed-in user gets <c>{ is_staff }</c> so the client can decide whether to show the admin UI.
    /// </summary>
    [HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        if (CurrentUserId is null)
            return Unauthorized(new ErrorResponse("invalid_token", "Invalid user identity"));

        return Ok(new AdminStatusResponse { IsStaff = await IsCurrentUserStaffAsync(cancellationToken) });
    }

    /// <summary>Every tenant with its member count (staff only).</summary>
    [HttpGet("tenants")]
    public async Task<IActionResult> ListTenants(CancellationToken cancellationToken)
    {
        var (_, denied) = await RequireStaffAsync(cancellationToken);
        if (denied is not null)
            return denied;

        var all = await tenants.ListAllAsync(cancellationToken);
        return Ok((IReadOnlyList<AdminTenantSummaryResponse>)all.Select(AdminTenantSummaryResponse.From).ToList());
    }

    /// <summary>One tenant's detail — members, subscription, audit volume (staff only; audited in-tenant).</summary>
    [HttpGet("tenants/{id:guid}")]
    public async Task<IActionResult> TenantDetail(Guid id, CancellationToken cancellationToken)
    {
        var (staffUserId, denied) = await RequireStaffAsync(cancellationToken);
        if (denied is not null)
            return denied;

        // Enter the target tenant so its ITenantScoped data reads through the normal filter (no bypass).
        using (tenantContext.EnterTenant(id))
        {
            var tenant = await tenants.GetByIdAsync(id, cancellationToken);
            if (tenant is null)
                return NotFound(new ErrorResponse("tenant_not_found", "Tenant not found"));

            var members = await tenants.GetMemberDetailsAsync(id, cancellationToken);
            var subscription = await subscriptions.Query().FirstOrDefaultAsync(cancellationToken);
            var auditCount = await auditEvents.Query().CountAsync(cancellationToken);

            // Loud, in-tenant audit: the tenant's owner can see a platform admin accessed the account.
            await audit.RecordAsync("admin.tenant.viewed", staffUserId, nameof(Tenant), id.ToString(), null, cancellationToken);
            await auditEvents.SaveChangesAsync(cancellationToken);

            return Ok(new AdminTenantDetailResponse
            {
                Id = tenant.Id,
                Name = tenant.Name,
                CreatedAt = tenant.CreatedAt,
                Members = members.Select(TenantMemberResponse.From).ToList(),
                SubscriptionStatus = subscription?.Status ?? "none",
                PlanKey = subscription?.PlanKey ?? PlanKeys.Free,
                ProviderManaged = subscription?.StripeSubscriptionId is not null,
                AuditEventCount = auditCount,
            });
        }
    }

    /// <summary>
    /// Comps a tenant onto a paid plan (staff only): writes the same <see cref="Subscription"/>
    /// projection a completed checkout produces — <c>active</c>, no <c>CurrentPeriodEnd</c> (a comp
    /// never lapses) — but with <b>no provider ids</b>. Refused with 409 when the tenant already has a
    /// live provider subscription: Stripe is the source of truth for real money (ADR-006) — manage
    /// those in the provider, not here. Audited in-tenant. Revert with the DELETE sibling.
    /// </summary>
    [HttpPut("tenants/{id:guid}/subscription")]
    public async Task<IActionResult> SetSubscription(Guid id, [FromBody] AdminSetSubscriptionRequest request, CancellationToken cancellationToken)
    {
        var (staffUserId, denied) = await RequireStaffAsync(cancellationToken);
        if (denied is not null)
            return denied;

        // A valid target is a known catalog plan other than Free (PlanCatalog.Get falls back to Free,
        // so require the round-trip key to match — an unknown key must 400, never silently comp Free).
        var planKey = request.PlanKey?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(planKey) || planKey == PlanKeys.Free || PlanCatalog.Get(planKey).Key != planKey)
            return BadRequest(new ErrorResponse("invalid_plan",
                $"plan_key must be a known paid plan (e.g. \"{PlanKeys.Pro}\"). To revert to Free, DELETE the subscription."));

        await using var scope = await unitOfWork.BeginTransactionAsync(cancellationToken);
        using (tenantContext.EnterTenant(id))
        {
            var tenant = await tenants.GetByIdAsync(id, cancellationToken);
            if (tenant is null)
                return NotFound(new ErrorResponse("tenant_not_found", "Tenant not found"));

            var subscription = await subscriptions.Query().FirstOrDefaultAsync(cancellationToken);
            if (subscription is { IsProviderManaged: true })
                return Conflict(new ErrorResponse("provider_managed",
                    "This tenant has a live provider subscription; manage its plan at the billing provider."));

            var now = clock.GetUtcNow();
            if (subscription is null)
            {
                // TenantId is stamped from the entered tenant by the write-stamping interceptor.
                await subscriptions.AddAsync(new Subscription
                {
                    PlanKey = planKey,
                    Status = SubscriptionStatus.Active,
                    CreatedAt = now,
                    UpdatedAt = now,
                }, cancellationToken);
            }
            else
            {
                subscription.PlanKey = planKey;
                subscription.Status = SubscriptionStatus.Active;
                subscription.CurrentPeriodEnd = null; // a comp never lapses
                subscription.UpdatedAt = now;
                subscriptions.Update(subscription);
            }

            await audit.RecordAsync("admin.subscription.comped", staffUserId, nameof(Subscription), id.ToString(),
                new { plan_key = planKey }, cancellationToken);
            await auditEvents.SaveChangesAsync(cancellationToken);
            await scope.CommitAsync(cancellationToken);

            return Ok(new AdminSubscriptionResponse { PlanKey = planKey, Status = SubscriptionStatus.Active });
        }
    }

    /// <summary>
    /// Reverts a staff comp: deletes the tenant's <see cref="Subscription"/> projection, and absence ⇒
    /// Free (the documented fail-closed default). Idempotent — reverting an already-Free tenant is a
    /// no-op 204. Refused with 409 for a provider-managed subscription (cancel it at the provider; the
    /// webhook then updates the projection). Audited in-tenant.
    /// </summary>
    [HttpDelete("tenants/{id:guid}/subscription")]
    public async Task<IActionResult> RemoveSubscription(Guid id, CancellationToken cancellationToken)
    {
        var (staffUserId, denied) = await RequireStaffAsync(cancellationToken);
        if (denied is not null)
            return denied;

        await using var scope = await unitOfWork.BeginTransactionAsync(cancellationToken);
        using (tenantContext.EnterTenant(id))
        {
            var tenant = await tenants.GetByIdAsync(id, cancellationToken);
            if (tenant is null)
                return NotFound(new ErrorResponse("tenant_not_found", "Tenant not found"));

            var subscription = await subscriptions.Query().FirstOrDefaultAsync(cancellationToken);
            if (subscription is null)
                return NoContent(); // already Free — nothing to revert
            if (subscription.IsProviderManaged)
                return Conflict(new ErrorResponse("provider_managed",
                    "This tenant has a live provider subscription; cancel it at the billing provider instead."));

            subscriptions.Remove(subscription);
            await audit.RecordAsync("admin.subscription.reverted", staffUserId, nameof(Subscription), id.ToString(),
                new { previous_plan = subscription.PlanKey }, cancellationToken);
            await auditEvents.SaveChangesAsync(cancellationToken);
            await scope.CommitAsync(cancellationToken);

            return NoContent();
        }
    }

    /// <summary>
    /// Sends an announcement to a tenant's members (staff only; ADMIN-3). With no <c>user_ids</c> it
    /// reaches every member; with a non-empty list it targets just those members (ids that aren't
    /// members of this tenant are ignored). Delivery goes through the normal notification fan-out —
    /// in-app row and/or outbox email per each user's preferences — and the send is loudly audited in
    /// the target tenant. Everything commits atomically.
    /// </summary>
    [HttpPost("tenants/{id:guid}/announce")]
    public async Task<IActionResult> Announce(Guid id, [FromBody] AdminAnnounceRequest request, CancellationToken cancellationToken)
    {
        var (staffUserId, denied) = await RequireStaffAsync(cancellationToken);
        if (denied is not null)
            return denied;

        var title = request.Title?.Trim();
        var body = request.Body?.Trim();
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(body)
            || title.Length > AnnounceTitleMaxLength || body.Length > AnnounceBodyMaxLength)
            return BadRequest(new ErrorResponse("invalid_request",
                $"Title (max {AnnounceTitleMaxLength} chars) and body (max {AnnounceBodyMaxLength} chars) are required."));

        // Notifications, outbox emails, and the in-tenant audit all commit together.
        await using var scope = await unitOfWork.BeginTransactionAsync(cancellationToken);
        using (tenantContext.EnterTenant(id))
        {
            var tenant = await tenants.GetByIdAsync(id, cancellationToken);
            if (tenant is null)
                return NotFound(new ErrorResponse("tenant_not_found", "Tenant not found"));

            // Targeting: a MISSING list (null) ⇒ every member; a PRESENT list ⇒ exactly its intersection
            // with actual membership — so an explicitly-empty [] notifies NO ONE, not everyone (v3 audit
            // LB-ADM-2). Intersecting also stops a stray/non-member id from reaching outside this tenant.
            var targeted = request.UserIds is not null;
            var members = await tenants.GetMembersAsync(id, cancellationToken);
            var recipients = (targeted
                ? members.Where(m => request.UserIds!.Contains(m.UserId))
                : members).ToList();

            foreach (var member in recipients)
                await notifications.NotifyAsync(member.UserId, "announcement", title, body,
                    metadata: null, cancellationToken);

            await audit.RecordAsync("admin.announcement.sent", staffUserId, nameof(Tenant), id.ToString(),
                new { member_count = recipients.Count, targeted }, cancellationToken);
            await auditEvents.SaveChangesAsync(cancellationToken);
            await scope.CommitAsync(cancellationToken);

            return Ok(new AdminAnnounceResponse { NotifiedCount = recipients.Count });
        }
    }

    /// <summary>
    /// Broadcasts an announcement to EVERY user across all tenants (staff only; ADMIN-3). The fan-out
    /// can be large, so it is enqueued on the outbox and delivered out-of-band — this returns 202
    /// immediately. Not tenant-audited: the action spans all tenants and the audit trail is per-tenant,
    /// so the durable outbox message is the record of the broadcast.
    /// </summary>
    [HttpPost("announce-all")]
    public async Task<IActionResult> AnnounceAll([FromBody] AdminAnnounceRequest request, CancellationToken cancellationToken)
    {
        var (staffUserId, denied) = await RequireStaffAsync(cancellationToken);
        if (denied is not null)
            return denied;

        var title = request.Title?.Trim();
        var body = request.Body?.Trim();
        if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(body)
            || title.Length > AnnounceTitleMaxLength || body.Length > AnnounceBodyMaxLength)
            return BadRequest(new ErrorResponse("invalid_request",
                $"Title (max {AnnounceTitleMaxLength} chars) and body (max {AnnounceBodyMaxLength} chars) are required."));

        // Stage the fan-out message and commit it. The handler (AdminBroadcastOutboxHandler) does the
        // per-user delivery out-of-band. This platform-wide broadcast spans all tenants so it has no
        // in-tenant audit row; the durable outbox message carries the acting staff id so the largest-
        // blast-radius admin write is still attributable (v3 audit ADM-6). SaveChangesAsync flushes the
        // staged message before commit (CommitAsync itself does not save).
        await using var scope = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await outbox.EnqueueAsync(AdminBroadcastOutboxHandler.MessageType,
            JsonSerializer.Serialize(new AdminBroadcastPayload(title, body, staffUserId)), tenantId: null, cancellationToken);
        await auditEvents.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);

        logger.LogWarning("admin.announce-all by staff {StaffUserId}", staffUserId); // platform-wide action → operational record
        return Accepted(new AdminBroadcastResponse { Status = "queued" });
    }

    /// <summary>
    /// "Sign in as" a user (staff only). Returns a <b>short-lived, non-refreshable</b> access token
    /// carrying the target's identity + an <c>impersonated_by</c> claim; loudly audited in the target's
    /// tenant. No refresh token is issued, so it expires on its own.
    /// </summary>
    [HttpPost("impersonate/{userId:guid}")]
    public async Task<IActionResult> Impersonate(Guid userId, CancellationToken cancellationToken)
    {
        var (staffUserId, denied) = await RequireStaffAsync(cancellationToken);
        if (denied is not null)
            return denied;

        var target = await users.GetByIdAsync(userId, cancellationToken);
        if (target is null)
            return NotFound(new ErrorResponse("user_not_found", "User not found"));

        var membership = await tenants.GetMembershipAsync(userId, cancellationToken);
        var tenantId = membership?.TenantId;
        var tenantName = tenantId is { } tid ? (await tenants.GetByIdAsync(tid, cancellationToken))?.Name : null;

        var token = jwt.IssueImpersonationToken(
            target.Id, target.Email, staffUserId, ImpersonationLifetime, target.DisplayName, tenantName, tenantId);

        // Loud, in-tenant audit so the target tenant can see a platform admin signed in as this user.
        if (tenantId is { } t)
            using (tenantContext.EnterTenant(t))
            {
                await audit.RecordAsync("admin.impersonation.started", staffUserId, nameof(User), userId.ToString(), null, cancellationToken);
                await auditEvents.SaveChangesAsync(cancellationToken);
            }

        return Ok(new ImpersonationResponse
        {
            AccessToken = token,
            ExpiresIn = (int)ImpersonationLifetime.TotalSeconds,
        });
    }

    /// <summary>
    /// Staff MFA reset (ADR-021 addendum): the recovery path for a user who lost BOTH the authenticator
    /// and the recovery codes — the possession proof the self-serve disable demands is exactly what they
    /// can no longer provide. Identity is verified <b>out-of-band</b> before calling this; the endpoint
    /// wipes the secret + recovery codes (the user re-enrolls from scratch) and makes the action loud:
    /// audited in the target's tenant (<c>admin.mfa.reset</c>, like impersonation) and the user is told
    /// through the normal notification fan-out (in-app + email), so a malicious reset cannot be silent.
    /// Idempotent — no MFA state is a no-op 204 with no audit/notification noise.
    /// </summary>
    [HttpDelete("users/{userId:guid}/mfa")]
    public async Task<IActionResult> ResetMfa(Guid userId, CancellationToken cancellationToken)
    {
        var (staffUserId, denied) = await RequireStaffAsync(cancellationToken);
        if (denied is not null)
            return denied;

        var target = await users.GetByIdAsync(userId, cancellationToken);
        if (target is null)
            return NotFound(new ErrorResponse("user_not_found", "User not found"));

        // Wipe, session revocation, notification, and the in-tenant audit commit atomically.
        await using var scope = await unitOfWork.BeginTransactionAsync(cancellationToken);
        if (!await mfaService.ResetAsync(userId, cancellationToken))
            return NoContent(); // nothing enrolled — idempotent no-op

        // Revoke the target's refresh tokens (v3 audit ADM-7): a reset is also a compromise-recovery
        // primitive, and an attacker's existing sessions would otherwise survive the second-factor wipe.
        // Enlists in this transaction (set-based UPDATE), so it commits with the wipe.
        await refreshTokens.RevokeAllUserTokensAsync(userId, cancellationToken);

        await notifications.NotifyAsync(userId, AdminMfaResetNotification.Kind,
            AdminMfaResetNotification.Title, AdminMfaResetNotification.Body,
            new { url = "/settings" }, cancellationToken);

        // Loud, in-tenant audit so the target tenant can see a platform admin touched this account
        // (a user without a tenant still gets the wipe + notification, like impersonation).
        var membership = await tenants.GetMembershipAsync(userId, cancellationToken);
        if (membership?.TenantId is { } tenantId)
            using (tenantContext.EnterTenant(tenantId))
            {
                await audit.RecordAsync("admin.mfa.reset", staffUserId, nameof(User), userId.ToString(), null, cancellationToken);
                await auditEvents.SaveChangesAsync(cancellationToken); // inside EnterTenant: the audit row stamps into the target tenant
            }
        else
        {
            // A tenant-less user has no tenant-scoped audit trail to write to (ADM-11 — a full platform-scoped
            // audit sink is deferred). Leave a durable operational record of this high-privilege staff action.
            logger.LogWarning("admin.mfa.reset by staff {StaffUserId} for tenant-less user {UserId}", staffUserId, userId);
            await auditEvents.SaveChangesAsync(cancellationToken); // flush the staged notification
        }

        await scope.CommitAsync(cancellationToken);
        return NoContent();
    }
}

/// <summary>Copy for the MFA-reset notification (server-side strings, like <see cref="BillingNotifications"/>).</summary>
public static class AdminMfaResetNotification
{
    public const string Kind = "security.mfa_reset";
    public const string Title = "Two-factor authentication was reset";
    public const string Body = "Platform support reset the two-factor authentication on your account "
        + "after verifying your identity. MFA is now disabled — re-enable it from Settings as soon as "
        + "possible. If you did not request this, contact support immediately.";
}
