using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Billing;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Services;

/// <summary>
/// Resolves and enforces plan quotas for the current tenant (BILLING-5). The plan is resolved from the
/// tenant's <see cref="Subscription"/> exactly like <see cref="EntitlementService"/> (fail-closed to Free);
/// seat usage counts members + pending invites; metered usage is a per-month counter. Limits live in the
/// <see cref="PlanCatalog"/> — a missing limit means unlimited, so this is inert until a plan sets one.
/// </summary>
public sealed class QuotaService(
    IRepository<Subscription> subscriptions,
    ITenantRepository tenants,
    ITenantInvitationRepository invitations,
    IRepository<UsageCounter> usage,
    ICurrentTenant currentTenant,
    TimeProvider clock) : IQuotaService
{
    public async Task<SeatUsage> GetSeatUsageAsync(CancellationToken cancellationToken = default)
    {
        var plan = await ResolvePlanAsync(cancellationToken);
        var tenantId = currentTenant.TenantId ?? Guid.Empty;

        // Seats = current members + still-pending invites (a pending invite is a reserved seat, so N
        // invites can't over-provision past the cap).
        var members = (await tenants.GetMembersAsync(tenantId, cancellationToken)).Count;
        var pending = (await invitations.GetPendingForTenantAsync(tenantId, cancellationToken)).Count;
        return new SeatUsage(members + pending, plan.SeatLimit);
    }

    public async Task<bool> CanAddSeatsAsync(int count = 1, CancellationToken cancellationToken = default)
        => (await GetSeatUsageAsync(cancellationToken)).CanAdd(count);

    public async Task<bool> TryConsumeAsync(string usageKey, int amount = 1, CancellationToken cancellationToken = default)
    {
        var plan = await ResolvePlanAsync(cancellationToken);
        if (plan.UsageLimit(usageKey) is not { } limit)
            return true; // unlimited for this key — allow without tracking
        if (amount <= 0)
            return true;
        if (amount > limit)
            return false; // a single request already exceeds the cap

        var now = clock.GetUtcNow();
        var period = now.ToString("yyyy-MM");

        // Atomic path: a single conditional UPDATE increments the existing counter only if it stays within
        // the cap. Postgres row-locks the counter, so concurrent consumers serialize and there is no
        // lost update (v2 audit LOGIC-B7); "atomically consumes" is now true under concurrency.
        if (await TryIncrementAsync(usageKey, period, amount, limit, now, cancellationToken))
            return true;

        // Nothing incremented: either the counter is at/over the cap, or there is no row yet this period.
        if (await usage.Query().AnyAsync(c => c.Key == usageKey && c.Period == period, cancellationToken))
            return false; // row exists → the cap is reached

        // First consume this period — create the row (amount <= limit, checked above). If a concurrent
        // request created it first, the unique (TenantId, Key, Period) index rejects our insert; detach it
        // and retry the conditional increment against the now-existing row.
        var row = new UsageCounter { Key = usageKey, Period = period, Count = amount, UpdatedAt = now };
        try
        {
            await usage.AddAsync(row, cancellationToken); // TenantId stamped by the interceptor
            await usage.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // ONLY a unique-(TenantId,Key,Period) violation means a concurrent request created the row first
            // — retry the conditional increment against it. Any OTHER DbUpdateException (serialization/
            // deadlock, timeout, a future check-constraint) is a real failure: it must propagate, not be
            // reinterpreted as a benign race that could spuriously deny a request with headroom (LB-BILL-3).
            usage.Remove(row); // detach the failed insert so it doesn't linger in the change tracker
            return await TryIncrementAsync(usageKey, period, amount, limit, now, cancellationToken);
        }
    }

    private async Task<bool> TryIncrementAsync(string usageKey, string period, int amount, int limit, DateTimeOffset now, CancellationToken cancellationToken) =>
        await usage.Query()
            .Where(c => c.Key == usageKey && c.Period == period && c.Count + amount <= limit)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Count, c => c.Count + amount)
                .SetProperty(c => c.UpdatedAt, now), cancellationToken) == 1;

    private async Task<Plan> ResolvePlanAsync(CancellationToken cancellationToken)
    {
        var subscription = await subscriptions.Query().FirstOrDefaultAsync(cancellationToken);
        return PlanCatalog.Get(EntitlementService.ResolvePlanKey(subscription, clock.GetUtcNow()));
    }
}
