using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;
using Vuelto.Infrastructure.Billing;

namespace Vuelto.Api.Services;

/// <summary>
/// Makes billing participate in tenant dissolve (BILLING-7, ADR-006 point 6). When a tenant is dissolved,
/// its <see cref="Subscription"/> projection is wiped <b>and</b> the provider subscription is canceled (so
/// Stripe stops charging a customer whose tenant no longer exists). The cancel is enqueued on the outbox —
/// staged with the dissolve, then run out-of-band with retry — rather than an external call inside the
/// teardown transaction. Not counted as "abandonable data": a subscription is billing plumbing, not tenant
/// content, so it never blocks a solo owner from leaving — it's cleaned up automatically instead.
/// </summary>
public sealed class BillingDataContributor(IRepository<Subscription> subscriptions, IOutbox outbox) : ITenantDataContributor
{
    // Billing is auto-canceled on dissolve, not user content — so it must not trip the "would abandon
    // data" guard that stops a solo owner from silently leaving real data behind.
    public Task<bool> HasDataAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public async Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        // Cross-tenant by design (dissolve runs for a tenant other than the current) — the audited hatch.
        var subscription = await subscriptions.QueryAllTenants()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        if (subscription is null)
            return;

        // Enqueue the provider cancel (idempotent, retried) if a live provider subscription exists. Staged
        // on the dissolve's unit of work; flush it so it survives the commit (the dissolve commits the
        // transaction without a SaveChanges, and set-based deletes below don't stage it for us).
        if (!string.IsNullOrEmpty(subscription.StripeSubscriptionId))
        {
            await outbox.EnqueueAsync(BillingCancelOutboxHandler.MessageType,
                JsonSerializer.Serialize(new BillingCancelPayload(subscription.StripeSubscriptionId)), tenantId, cancellationToken);
            await subscriptions.SaveChangesAsync(cancellationToken); // flush within the tx (not a commit)
        }

        // Query() (not QueryAllTenants): the dissolve enters the target tenant (RLS-2/T6), so the filter
        // scopes this to it; composing QueryAllTenants() with a set-based write is banned (RLS-4/T7).
        await subscriptions.Query()
            .Where(s => s.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public string ExportKey => "billing";

    // Billing state for the export (GDPR-1) — plan/status/period only; never the Stripe ids or card data.
    public async Task<object?> ExportAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var subscription = await subscriptions.QueryAllTenants()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
        return subscription is null
            ? null
            : new { subscription.PlanKey, subscription.Status, subscription.CurrentPeriodEnd, subscription.CreatedAt };
    }
}
