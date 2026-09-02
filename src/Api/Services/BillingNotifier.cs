using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Perezosoft.Api.Services;

/// <summary>
/// Sends billing lifecycle notifications (dunning) to a tenant's **owner** — the account holder
/// responsible for the subscription (BILLING-6, ADR-006). Reuses the per-user notification center
/// (NOTIFY, ADR-013): the in-app row is staged on the caller's unit of work and the email copy rides the
/// outbox, per the owner's delivery preferences. Callers run inside the target tenant's scope
/// (<c>EnterTenant</c>) so the owner lookup resolves.
/// </summary>
public interface IBillingNotifier
{
    Task NotifyOwnerAsync(Guid tenantId, string kind, string title, string body, CancellationToken cancellationToken = default);
}

public sealed class BillingNotifier(ITenantRepository tenants, INotificationService notifications) : IBillingNotifier
{
    public async Task NotifyOwnerAsync(Guid tenantId, string kind, string title, string body, CancellationToken cancellationToken = default)
    {
        var members = await tenants.GetMembersAsync(tenantId, cancellationToken);
        var owner = members.FirstOrDefault(m => m.Role == TenantRoles.Owner);
        if (owner is null)
            return; // no owner to notify (e.g. mid-dissolve) — nothing to do

        // Link the notification to the billing page so the owner can act (update card / resubscribe).
        await notifications.NotifyAsync(owner.UserId, kind, title, body, new { url = "/billing" }, cancellationToken);
    }
}

/// <summary>Notification kinds + copy for billing lifecycle events (BILLING-6). Server-side strings.</summary>
public static class BillingNotifications
{
    public const string PastDueKind = "billing.past_due";
    public const string CanceledKind = "billing.canceled";
    public const string LapsedKind = "billing.lapsed";

    public static (string Title, string Body) PastDue => (
        "Payment failed",
        "We couldn't process your latest payment. Please update your payment method to keep your subscription active.");

    public static (string Title, string Body) Canceled => (
        "Subscription canceled",
        "Your subscription has been canceled. You can resubscribe any time from the billing page.");

    public static (string Title, string Body) Lapsed => (
        "Subscription expired",
        "Your subscription period has ended and hasn't renewed. Resubscribe from the billing page to restore your plan.");
}
