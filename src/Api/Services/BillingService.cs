using Microsoft.EntityFrameworkCore;
using Perezosoft.Api.Configuration;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Billing;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Perezosoft.Api.Services;

public enum CheckoutOutcome { Created, InvalidPlan }

public sealed record CheckoutResult(CheckoutOutcome Outcome, string? Url);

public enum PortalOutcome { Created, NoSubscription }

public sealed record PortalResult(PortalOutcome Outcome, string? Url);

/// <summary>
/// Billing orchestration behind the platform <c>BillingController</c> (ADR-006): creates hosted
/// checkout (BILLING-2) and billing-portal (BILLING-4) sessions via the configured
/// <see cref="IBillingProvider"/>. Owner-only access is enforced by the controller
/// (<c>TenantApiControllerBase</c>). Checkout writes no <c>Subscription</c> — access is granted only by
/// the BILLING-3 webhook; portal changes also flow back through that webhook.
/// </summary>
public interface IBillingService
{
    Task<CheckoutResult> CreateCheckoutAsync(Guid tenantId, string? planKey, CancellationToken cancellationToken = default);

    /// <summary>Opens the billing-management portal for the current tenant's customer, or reports no subscription.</summary>
    Task<PortalResult> CreatePortalAsync(CancellationToken cancellationToken = default);
}

public sealed class BillingService(
    IBillingProvider billing,
    IApplicationSettings app,
    IRepository<Subscription> subscriptions) : IBillingService
{
    public async Task<CheckoutResult> CreateCheckoutAsync(Guid tenantId, string? planKey, CancellationToken cancellationToken = default)
    {
        if (!IsPurchasablePlan(planKey))
            return new(CheckoutOutcome.InvalidPlan, null);

        var baseUrl = app.ClientUrl.TrimEnd('/');
        var session = await billing.CreateCheckoutSessionAsync(
            new BillingCheckoutRequest(tenantId, planKey!, $"{baseUrl}/billing/success", $"{baseUrl}/billing/cancel"),
            cancellationToken);

        return new(CheckoutOutcome.Created, session.Url);
    }

    public async Task<PortalResult> CreatePortalAsync(CancellationToken cancellationToken = default)
    {
        // Query() is scoped to the current (caller's) tenant, so we read this tenant's customer id.
        var subscription = await subscriptions.Query().FirstOrDefaultAsync(cancellationToken);
        if (subscription?.StripeCustomerId is not { } customerId)
            return new(PortalOutcome.NoSubscription, null);

        var session = await billing.CreatePortalSessionAsync(
            new BillingPortalRequest(customerId, $"{app.ClientUrl.TrimEnd('/')}/billing"), cancellationToken);

        return new(PortalOutcome.Created, session.Url);
    }

    /// <summary>A non-Free plan that actually exists in the catalog (unknown keys resolve to Free).</summary>
    private static bool IsPurchasablePlan(string? planKey) =>
        !string.IsNullOrWhiteSpace(planKey) && planKey != PlanKeys.Free && PlanCatalog.Get(planKey).Key == planKey;
}
