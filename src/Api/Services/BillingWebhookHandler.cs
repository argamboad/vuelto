using Microsoft.EntityFrameworkCore;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Perezosoft.Api.Services;

public enum WebhookResult { Applied, Duplicate, Ignored, InvalidSignature }

/// <summary>
/// Applies inbound billing webhooks to the tenant's <see cref="Subscription"/> projection (BILLING-3,
/// ADR-006). The flow is: <b>verify signature</b> → <b>dedup via the inbox</b> (ADR-007) →
/// <b>EnterTenant</b> (ADR-003) → <b>upsert</b> through the normal tenant-scoped path. The inbox claim
/// and the projection write commit in one transaction, so a redelivery re-processes only if the apply
/// failed. Completing checkout grants nothing — THIS is what flips entitlements.
/// </summary>
public sealed class BillingWebhookHandler(
    IBillingProvider provider,
    IInbox inbox,
    IRepository<Subscription> subscriptions,
    ITenantContext tenantContext,
    IUnitOfWork unitOfWork,
    IBillingNotifier billingNotifier,
    TimeProvider clock)
{
    public async Task<WebhookResult> HandleAsync(string payload, string? signature, CancellationToken cancellationToken = default)
    {
        BillingWebhookEvent? evt;
        try
        {
            evt = provider.ParseWebhookEvent(payload, signature);
        }
        catch (BillingWebhookSignatureException)
        {
            return WebhookResult.InvalidSignature; // not authentic — reject, apply nothing
        }

        if (evt is null)
            return WebhookResult.Ignored; // authentic but irrelevant — acknowledge

        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);

        // At-least-once + out-of-order delivery: claim the event id so a redelivery is a no-op.
        if (!await inbox.TryClaimAsync("stripe", evt.EventId, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return WebhookResult.Duplicate;
        }

        // No JWT here — enter the (signature-authenticated) tenant so the write is stamped + scoped
        // by the normal interceptor/filter, not the cross-tenant escape hatch (ADR-003).
        bool applied;
        using (tenantContext.EnterTenant(evt.TenantId))
        {
            string? previousStatus;
            (applied, previousStatus) = await UpsertSubscriptionAsync(evt, cancellationToken);
            // Dunning (BILLING-6): notify the owner when the status *transitions into* a bad state — a
            // failed payment or a cancellation. Only fire on an *applied* event (a stale/out-of-order
            // event is a no-op, so it must not re-notify); same-status redeliveries don't re-notify.
            if (applied)
                await MaybeNotifyDunningAsync(evt, previousStatus, cancellationToken);
            await transaction.CommitAsync(cancellationToken); // claim + projection + notification commit atomically
        }

        // A stale/out-of-order event is authentic and claimed, but superseded — acknowledged, not applied.
        return applied ? WebhookResult.Applied : WebhookResult.Ignored;
    }

    /// <summary>
    /// Upserts the projection. Returns whether the event was applied and the status the projection had
    /// before it. A stale/out-of-order event (not strictly newer than the last applied) is a no-op —
    /// <c>Applied</c> is false — so it never regresses state (v2 audit LOGIC-B1).
    /// </summary>
    private async Task<(bool Applied, string? PreviousStatus)> UpsertSubscriptionAsync(BillingWebhookEvent evt, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var subscription = await subscriptions.Query().FirstOrDefaultAsync(cancellationToken); // entered-tenant scoped

        // Recency guard: webhooks are at-least-once and unordered, so apply only STRICTLY-newer events.
        // Strictly older (<), not `<=` (v3 audit LB-BILL-1): Stripe's Created is whole-second, so two
        // DISTINCT events in the same second (e.g. checkout created+updated, a rapid plan change) must both
        // apply — dropping the newer one as "stale" could leave a paid tenant on Free. Exact REDELIVERY is
        // already caught upstream by the inbox (by EventId), so it never reaches here to be double-applied.
        if (subscription is not null && subscription.LastEventAt is { } last && evt.OccurredAt < last)
            return (false, subscription.Status);

        var previousStatus = subscription?.Status;

        if (subscription is null)
        {
            await subscriptions.AddAsync(new Subscription
            {
                PlanKey = evt.PlanKey,
                Status = evt.Status,
                StripeCustomerId = evt.StripeCustomerId,
                StripeSubscriptionId = evt.StripeSubscriptionId,
                CurrentPeriodEnd = evt.CurrentPeriodEnd,
                LastEventAt = evt.OccurredAt,
                CreatedAt = now,
                UpdatedAt = now,
            }, cancellationToken); // TenantId stamped to the entered tenant by the interceptor
        }
        else
        {
            subscription.PlanKey = evt.PlanKey;
            subscription.Status = evt.Status;
            subscription.StripeCustomerId = evt.StripeCustomerId;
            subscription.StripeSubscriptionId = evt.StripeSubscriptionId;
            subscription.CurrentPeriodEnd = evt.CurrentPeriodEnd;
            subscription.LastEventAt = evt.OccurredAt;
            subscription.UpdatedAt = now;
            subscriptions.Update(subscription);
        }

        await subscriptions.SaveChangesAsync(cancellationToken);
        return (true, previousStatus);
    }

    private async Task MaybeNotifyDunningAsync(BillingWebhookEvent evt, string? previousStatus, CancellationToken cancellationToken)
    {
        // Dun only on a transition OUT OF a granting (live) subscription — never a cold start (LB-BILL-4):
        // the FIRST-ever event for a tenant can carry past_due/canceled (a failed first invoice, an
        // abandoned checkout later canceled), and previousStatus is then null, so a bare `Status != previous`
        // would notify a tenant that never had a live subscription. Also skips a non-transition.
        if (evt.Status == previousStatus || !SubscriptionStatus.IsGranting(previousStatus))
            return;

        var copy = evt.Status switch
        {
            SubscriptionStatus.PastDue => BillingNotifications.PastDue,
            SubscriptionStatus.Canceled => BillingNotifications.Canceled,
            _ => default,
        };
        if (copy == default)
            return; // active/trialing transitions aren't dunning events

        var kind = evt.Status == SubscriptionStatus.PastDue
            ? BillingNotifications.PastDueKind
            : BillingNotifications.CanceledKind;

        await billingNotifier.NotifyOwnerAsync(evt.TenantId, kind, copy.Title, copy.Body, cancellationToken);
        await subscriptions.SaveChangesAsync(cancellationToken); // flush the staged notification within the tx
    }
}
