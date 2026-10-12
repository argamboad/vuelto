using System.Collections.Concurrent;
using System.Text.Json;
using Vuelto.Core.Abstractions;

namespace Vuelto.Infrastructure.Billing;

/// <summary>
/// In-memory <see cref="IBillingProvider"/> for tests and for dev when no Stripe key is configured
/// (ADR-006): returns a deterministic fake checkout URL and records each request, so behaviour can be
/// asserted offline with zero real charges. Keeps the app bootable with no Stripe setup.
/// <para>
/// <see cref="ParseWebhookEvent"/> treats the literal signature <see cref="ValidSignature"/> as
/// authentic and deserializes the payload as a <see cref="BillingWebhookEvent"/> JSON (an empty body
/// means an authentic-but-irrelevant event → null); any other signature throws, so webhook handling can
/// be driven end-to-end with no Stripe.
/// </para>
/// </summary>
public sealed class FakeBillingProvider : IBillingProvider
{
    /// <summary>The signature value the fake accepts as authentic.</summary>
    public const string ValidSignature = "valid";

    /// <summary>Every checkout request received, for assertions.</summary>
    public ConcurrentQueue<BillingCheckoutRequest> Requests { get; } = new();

    /// <summary>Every portal request received, for assertions.</summary>
    public ConcurrentQueue<BillingPortalRequest> PortalRequests { get; } = new();

    /// <summary>Every subscription id a cancel was requested for, for assertions.</summary>
    public ConcurrentQueue<string> CanceledSubscriptions { get; } = new();

    public Task<BillingCheckoutSession> CreateCheckoutSessionAsync(BillingCheckoutRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Enqueue(request);
        return Task.FromResult(new BillingCheckoutSession($"https://billing.test/checkout/{request.TenantId}/{request.PlanKey}"));
    }

    public Task<BillingPortalSession> CreatePortalSessionAsync(BillingPortalRequest request, CancellationToken cancellationToken = default)
    {
        PortalRequests.Enqueue(request);
        return Task.FromResult(new BillingPortalSession($"https://billing.test/portal/{request.StripeCustomerId}"));
    }

    public BillingWebhookEvent? ParseWebhookEvent(string payload, string? signature)
    {
        if (signature != ValidSignature)
            throw new BillingWebhookSignatureException("Fake billing: invalid signature.");

        return string.IsNullOrWhiteSpace(payload)
            ? null // authentic but irrelevant
            : JsonSerializer.Deserialize<BillingWebhookEvent>(payload);
    }

    public Task CancelSubscriptionAsync(string stripeSubscriptionId, CancellationToken cancellationToken = default)
    {
        CanceledSubscriptions.Enqueue(stripeSubscriptionId);
        return Task.CompletedTask;
    }
}
