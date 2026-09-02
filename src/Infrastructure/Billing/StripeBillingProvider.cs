using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Billing;
using Perezosoft.Core.Entities;

namespace Perezosoft.Infrastructure.Billing;

/// <summary>
/// Stripe reference implementation of <see cref="IBillingProvider"/> (ADR-006). Creates a hosted
/// Checkout session in subscription mode for the plan's configured price, tagging it with the tenant
/// id (<c>ClientReferenceId</c> + metadata) so the webhook (BILLING-3) reconciles it. No card data
/// touches our system — the money mutation happens on Stripe.
/// </summary>
public sealed class StripeBillingProvider(IOptions<StripeSettings> options) : IBillingProvider
{
    private readonly StripeSettings _settings = options.Value;

    public async Task<BillingCheckoutSession> CreateCheckoutSessionAsync(BillingCheckoutRequest request, CancellationToken cancellationToken = default)
    {
        if (!_settings.Prices.TryGetValue(request.PlanKey, out var priceId) || string.IsNullOrWhiteSpace(priceId))
            throw new InvalidOperationException($"No Stripe price configured for plan '{request.PlanKey}' (Billing:Stripe:Prices).");

        // apiBase is null in prod (real Stripe) and set to stripe-mock in tests.
        var client = new StripeClient(_settings.SecretKey, apiBase: _settings.ApiBase);
        var sessions = new SessionService(client);

        var session = await sessions.CreateAsync(new SessionCreateOptions
        {
            Mode = "subscription",
            LineItems = [new SessionLineItemOptions { Price = priceId, Quantity = 1 }],
            SuccessUrl = request.SuccessUrl,
            CancelUrl = request.CancelUrl,
            ClientReferenceId = request.TenantId.ToString(),
            Metadata = new Dictionary<string, string> { ["tenant_id"] = request.TenantId.ToString() },
            // Stamp the SUBSCRIPTION too, so customer.subscription.* webhooks carry the tenant id.
            SubscriptionData = new SessionSubscriptionDataOptions
            {
                Metadata = new Dictionary<string, string> { ["tenant_id"] = request.TenantId.ToString() },
            },
        }, cancellationToken: cancellationToken);

        return new BillingCheckoutSession(session.Url);
    }

    public async Task<BillingPortalSession> CreatePortalSessionAsync(BillingPortalRequest request, CancellationToken cancellationToken = default)
    {
        var client = new StripeClient(_settings.SecretKey, apiBase: _settings.ApiBase);
        var sessions = new Stripe.BillingPortal.SessionService(client);

        var session = await sessions.CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
        {
            Customer = request.StripeCustomerId,
            ReturnUrl = request.ReturnUrl,
        }, cancellationToken: cancellationToken);

        return new BillingPortalSession(session.Url);
    }

    public BillingWebhookEvent? ParseWebhookEvent(string payload, string? signature)
    {
        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(payload, signature, _settings.WebhookSecret);
        }
        catch (StripeException ex)
        {
            throw new BillingWebhookSignatureException($"Invalid Stripe webhook signature: {ex.Message}");
        }

        // We reconcile only subscription-lifecycle events; everything else is acknowledged and ignored.
        if (stripeEvent.Data.Object is not Stripe.Subscription subscription)
            return null;

        if (!subscription.Metadata.TryGetValue("tenant_id", out var tenantRaw) || !Guid.TryParse(tenantRaw, out var tenantId))
            return null; // can't reconcile without our tenant tag

        // Stripe's 2025 API moved current_period_end onto the subscription item.
        var item = subscription.Items?.Data?.FirstOrDefault();
        var planKey = item?.Price?.Id is { } priceId ? _settings.PlanForPrice(priceId) : null;

        return new BillingWebhookEvent(
            EventId: stripeEvent.Id,
            TenantId: tenantId,
            PlanKey: planKey ?? PlanKeys.Free,
            Status: MapStatus(subscription.Status),
            StripeCustomerId: subscription.CustomerId,
            StripeSubscriptionId: subscription.Id,
            CurrentPeriodEnd: item?.CurrentPeriodEnd is { } end ? new DateTimeOffset(end, TimeSpan.Zero) : null,
            OccurredAt: new DateTimeOffset(stripeEvent.Created, TimeSpan.Zero)); // provider emission time — recency guard
    }

    public async Task CancelSubscriptionAsync(string stripeSubscriptionId, CancellationToken cancellationToken = default)
    {
        var client = new StripeClient(_settings.SecretKey, apiBase: _settings.ApiBase);
        var subscriptions = new SubscriptionService(client);
        try
        {
            await subscriptions.CancelAsync(stripeSubscriptionId, cancellationToken: cancellationToken);
        }
        catch (StripeException ex) when (ex.StripeError?.Type == "invalid_request_error")
        {
            // Already canceled / unknown id — the effect is already achieved; don't fail the retry.
        }
    }

    /// <summary>Map Stripe's subscription status to our (fail-closed) <see cref="SubscriptionStatus"/>.</summary>
    private static string MapStatus(string stripeStatus) => stripeStatus switch
    {
        "active" => SubscriptionStatus.Active,
        "trialing" => SubscriptionStatus.Trialing,
        "past_due" or "unpaid" => SubscriptionStatus.PastDue,
        _ => SubscriptionStatus.Canceled, // canceled / incomplete / incomplete_expired → fail closed
    };
}
