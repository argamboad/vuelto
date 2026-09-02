using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Services;

/// <summary>
/// Scheduled sweep (BILLING-6, ADR-007) that catches subscriptions whose paid period **lapsed** without
/// a corresponding webhook (a trial that ended, or a renewal that didn't happen and no <c>past_due</c>/
/// <c>canceled</c> event arrived). Entitlements already fail closed on a past period end, so this doesn't
/// change access — it sends the owner a one-time courtesy nudge to resubscribe. Stripe stays the source of
/// truth: the sweep never fabricates a status, it only records that it notified (<c>LapseNotifiedAt</c>),
/// so it fires once per lapse. Scans every tenant via the audited cross-tenant queryable, then notifies
/// inside each tenant's scope.
/// </summary>
public sealed class SubscriptionLapseSweepJob(
    IRepository<Subscription> subscriptions,
    ITenantContext tenantContext,
    IBillingNotifier billingNotifier,
    TimeProvider clock,
    ILogger<SubscriptionLapseSweepJob> logger) : IScheduledJob
{
    public string Name => "subscription-lapse-sweep";
    public TimeSpan Interval => TimeSpan.FromHours(6);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();

        // Lapsed = still marked active/trialing, but the paid period ended, and we haven't nudged for
        // THIS period yet (LapseNotifiedAt null or from a previous period).
        var lapsed = await subscriptions.QueryAllTenants()
            .Where(s => (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing)
                        && s.CurrentPeriodEnd != null && s.CurrentPeriodEnd < now
                        && (s.LapseNotifiedAt == null || s.LapseNotifiedAt < s.CurrentPeriodEnd))
            .ToListAsync(cancellationToken);

        foreach (var sub in lapsed)
        {
            try
            {
                using (tenantContext.EnterTenant(sub.TenantId))
                {
                    var (title, body) = BillingNotifications.Lapsed;
                    await billingNotifier.NotifyOwnerAsync(sub.TenantId, BillingNotifications.LapsedKind, title, body, cancellationToken);
                    sub.LapseNotifiedAt = now;
                    subscriptions.Update(sub);
                    await subscriptions.SaveChangesAsync(cancellationToken); // notification + stamp commit together
                }
            }
            catch (Exception ex)
            {
                // Isolate per-tenant failures so one bad row doesn't stop the sweep (IScheduledJob contract).
                logger.LogError(ex, "Lapse nudge failed for tenant {TenantId}", sub.TenantId);
            }
        }
    }
}
