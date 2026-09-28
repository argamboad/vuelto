using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Billing;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Billing;

/// <summary>
/// Stripe reference implementation of <see cref="IBillingProvider"/> (ADR-006). Creates a hosted
/// Checkout session in subscription mode for the plan's configured price, tagging it with the tenant
/// id (<c>ClientReferenceId</c> + metadata) so the webhook (BILLING-3) reconciles it. No card data
/// touches our system — the money mutation happens on Stripe.
/// </summary>
public sealed class StripeBillingProvider(IOptions<StripeSettings> options, ILogger<StripeBillingProvider>? logger = null) : IBillingProvider
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

        // A test-mode event signed with the secret this deployment trusts must not move a real tenant's plan
        // (v4 T46): with a test-mode signing secret left in production, Dashboard test events are authentic.
        if (_settings.ExpectsLiveEvents is { } expectLive && stripeEvent.Livemode != expectLive)
        {
            logger?.LogWarning("Stripe event {EventId} is {Mode} but this deployment expects {Expected} events: ignored",
                stripeEvent.Id, stripeEvent.Livemode ? "live" : "test", expectLive ? "live" : "test");
            return null;
        }

        // We reconcile only subscription-lifecycle events; everything else is acknowledged and ignored.
        if (stripeEvent.Data.Object is not Stripe.Subscription subscription)
            return null;

        if (!subscription.Metadata.TryGetValue("tenant_id", out var tenantRaw) || !Guid.TryParse(tenantRaw, out var tenantId))
            return null; // can't reconcile without our tenant tag

        // Stripe's 2025 API moved current_period_end onto the subscription item.
        var item = subscription.Items?.Data?.FirstOrDefault();

        // A price this deployment does not sell (Billing:Stripe:Prices) is a configuration gap, not "Free": applied
        // as Free it would leave a paying tenant on the free plan with a "Your Free plan is active" email, and staff
        // could not comp around it because the row is provider-managed (v4 T46). Acknowledged, not applied, loud.
        string? planKey = null;
        if (item?.Price?.Id is { } priceId)
        {
            planKey = _settings.PlanForPrice(priceId);
            if (planKey is null)
            {
                logger?.LogError("Stripe event {EventId} for tenant {TenantId} carries price {PriceId}, which maps to no plan in Billing:Stripe:Prices: ignored, not applied",
                    stripeEvent.Id, tenantId, priceId);
                return null;
            }
        }

        // Stripe.net's field is a non-nullable DateTime: an absent current_period_end arrives as a sentinel (the Unix
        // epoch from its converter, or default), which the projection would store and every reader would treat as
        // a lapsed period (v4 T46). Null, with a warning.
        DateTimeOffset? periodEnd = null;
        if (item is not null)
        {
            if (item.CurrentPeriodEnd <= DateTime.UnixEpoch)
                logger?.LogWarning("Stripe event {EventId} for tenant {TenantId} carries no current_period_end: stored as open-ended", stripeEvent.Id, tenantId);
            else
                periodEnd = new DateTimeOffset(item.CurrentPeriodEnd, TimeSpan.Zero);
        }

        return new BillingWebhookEvent(
            EventId: stripeEvent.Id,
            TenantId: tenantId,
            PlanKey: planKey ?? PlanKeys.Free,
            Status: MapStatus(subscription.Status),
            StripeCustomerId: subscription.CustomerId,
            StripeSubscriptionId: subscription.Id,
            CurrentPeriodEnd: periodEnd,
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
