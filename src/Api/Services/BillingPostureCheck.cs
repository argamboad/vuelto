using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Configuration;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Services;

/// <summary>
/// One line at startup (v4 T46, BILL-1): plan resolution is <b>gate-blind on purpose</b> — an active
/// subscription row resolves to its plan whether or not <c>Billing:Enabled</c> is on, so flipping the gate never
/// silently downgrades anyone — but with the gate off the provider webhook route is gone, so a cancellation at
/// Stripe cannot reach the projection: a cancelled Pro tenant stays Pro, a comp never lapses, and Stripe keeps
/// charging. Nothing else would say so. This counts the Stripe-managed rows and warns when the gate is off.
/// </summary>
public sealed class BillingPostureCheck(BillingSettings billing, IServiceScopeFactory scopes, ILogger<BillingPostureCheck> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (billing.Enabled)
            return;

        using var scope = scopes.CreateScope();
        var subscriptions = scope.ServiceProvider.GetRequiredService<IRepository<Subscription>>();
        // A cross-tenant READ through the sanctioned escape hatch (ADR-003); counts only, no tenant data.
        var stripeManaged = await subscriptions.QueryAllTenants()
            .CountAsync(s => s.StripeSubscriptionId != null && s.Status != SubscriptionStatus.Canceled, cancellationToken);

        if (stripeManaged > 0)
            logger.LogWarning(
                "Billing is off but {Count} tenant(s) hold a live Stripe-managed subscription. They keep their plan (resolution is gate-blind), "
                + "but the webhook route is gone: a cancellation at Stripe will not reach the projection and Stripe keeps charging. "
                + "Cancel them at Stripe, or turn Billing:Enabled on.", stripeManaged);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
