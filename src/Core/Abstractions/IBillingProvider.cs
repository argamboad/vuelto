namespace Vuelto.Core.Abstractions;

/// <summary>
/// Abstraction over the payment provider (ADR-006), mirroring <c>IEmailSender</c>'s Core-abstraction
/// shape so Core stays provider-agnostic and tests run offline. The Stripe reference implementation
/// lives in Infrastructure; <c>FakeBillingProvider</c> is the in-memory test/dev fallback. Money
/// mutations happen on the provider's hosted Checkout — we never build card forms or store card data.
/// </summary>
public interface IBillingProvider
{
    /// <summary>
    /// Creates a hosted checkout session to subscribe the tenant to a plan and returns its URL. The
    /// session carries the tenant id so the resulting webhook (BILLING-3) can reconcile it back to the
    /// tenant. Access is NOT granted by completing checkout — only the webhook flips the subscription.
    /// </summary>
    Task<BillingCheckoutSession> CreateCheckoutSessionAsync(BillingCheckoutRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a hosted billing-management portal session for an existing customer (manage/upgrade/
    /// cancel) and returns its URL. Resulting changes flow back through the webhook, so there is no
    /// separate state logic here.
    /// </summary>
    Task<BillingPortalSession> CreatePortalSessionAsync(BillingPortalRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies an inbound webhook's authenticity (signature) and maps it to a provider-agnostic
    /// <see cref="BillingWebhookEvent"/>, or <c>null</c> for an authentic-but-irrelevant event (the
    /// caller acknowledges it). Throws <see cref="BillingWebhookSignatureException"/> when the payload
    /// is not authentic — the caller must reject those (HTTP 400) and apply nothing.
    /// </summary>
    BillingWebhookEvent? ParseWebhookEvent(string payload, string? signature);

    /// <summary>
    /// Cancels a subscription at the provider (BILLING-7) — used when a tenant is dissolved so the customer
    /// stops being billed. Idempotent: canceling an already-canceled/absent subscription is a no-op, not an
    /// error (delivery is at-least-once via the outbox).
    /// </summary>
    Task CancelSubscriptionAsync(string stripeSubscriptionId, CancellationToken cancellationToken = default);
}

/// <param name="TenantId">The tenant being subscribed — carried into the session for webhook reconciliation.</param>
/// <param name="PlanKey">Plan to subscribe to (a <c>PlanKeys</c> value); the provider maps it to a price.</param>
/// <param name="SuccessUrl">Where the provider returns the user after a completed checkout.</param>
/// <param name="CancelUrl">Where the provider returns the user if they cancel.</param>
public sealed record BillingCheckoutRequest(Guid TenantId, string PlanKey, string SuccessUrl, string CancelUrl);

/// <summary>The hosted checkout URL the client redirects to.</summary>
public sealed record BillingCheckoutSession(string Url);

/// <param name="StripeCustomerId">The provider customer to open the management portal for (from the <c>Subscription</c> projection).</param>
/// <param name="ReturnUrl">Where the provider returns the user when they leave the portal.</param>
public sealed record BillingPortalRequest(string StripeCustomerId, string ReturnUrl);

/// <summary>The hosted billing-portal URL the client redirects to.</summary>
public sealed record BillingPortalSession(string Url);

/// <summary>
/// A provider-agnostic projection of a subscription-lifecycle webhook — what the platform needs to
/// reconcile a tenant's <c>Subscription</c>. The provider resolves the tenant id (from the metadata we
/// stamped at checkout), the plan (from the price), and the normalized status.
/// </summary>
/// <param name="EventId">The provider's unique event id — the inbox idempotency key (ADR-007).</param>
/// <param name="TenantId">The tenant this event reconciles, from the signed metadata.</param>
/// <param name="PlanKey">The plan the subscription grants (a <c>PlanKeys</c> value).</param>
/// <param name="Status">Normalized status — a <c>SubscriptionStatus</c> value (active/trialing/past_due/canceled).</param>
/// <param name="OccurredAt">When the provider emitted this event. Webhooks are at-least-once and unordered,
/// so the handler applies an event only if it is strictly newer than the last one applied (v2 audit LOGIC-B1).</param>
public sealed record BillingWebhookEvent(
    string EventId,
    Guid TenantId,
    string PlanKey,
    string Status,
    string? StripeCustomerId,
    string? StripeSubscriptionId,
    DateTimeOffset? CurrentPeriodEnd,
    DateTimeOffset OccurredAt);

/// <summary>Thrown when a webhook payload fails signature verification (not authentic).</summary>
public sealed class BillingWebhookSignatureException(string message) : Exception(message);
