using Microsoft.EntityFrameworkCore;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Billing;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Perezosoft.Api.Services;

/// <summary>
/// Resolves entitlements from the current tenant's <see cref="Subscription"/> projection. The
/// repository's <c>Query()</c> is already tenant-scoped (global filter), so this reads only the
/// current tenant's row. Plan resolution **fails closed to Free**: no subscription, a non-active/
/// -trialing status, or a lapsed period all collapse to Free (ADR-006).
/// </summary>
public sealed class EntitlementService(IRepository<Subscription> subscriptions, TimeProvider clock) : IEntitlementService
{
    public async Task<bool> HasAsync(string entitlementKey, CancellationToken cancellationToken = default)
    {
        var subscription = await subscriptions.Query().FirstOrDefaultAsync(cancellationToken);
        var planKey = ResolvePlanKey(subscription, clock.GetUtcNow());
        return PlanCatalog.Get(planKey).Includes(entitlementKey);
    }

    /// <summary>The active plan, or Free if the subscription is absent/inactive/lapsed (fail-closed).</summary>
    internal static string ResolvePlanKey(Subscription? subscription, DateTimeOffset now)
    {
        if (subscription is null)
            return PlanKeys.Free;

        var active = subscription.Status is SubscriptionStatus.Active or SubscriptionStatus.Trialing
                     && (subscription.CurrentPeriodEnd is null || subscription.CurrentPeriodEnd > now);

        return active ? subscription.PlanKey : PlanKeys.Free;
    }
}
