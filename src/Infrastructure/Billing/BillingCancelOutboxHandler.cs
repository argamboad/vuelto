using System.Text.Json;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Billing;

/// <summary>The outbox payload for canceling a subscription at the provider (BILLING-7).</summary>
public sealed record BillingCancelPayload(string StripeSubscriptionId);

/// <summary>
/// Outbox handler for <c>"billing.cancel"</c> messages (BILLING-7, ADR-006): cancels a subscription at the
/// provider when a tenant is dissolved, so the customer stops being billed. Runs out-of-band (the
/// dissolve enqueues it, staged with the teardown) rather than calling the provider inside the dissolve
/// transaction. Idempotent — the provider treats an already-canceled/absent subscription as a no-op — so
/// the outbox's at-least-once retry is safe.
/// </summary>
public sealed class BillingCancelOutboxHandler(IBillingProvider provider) : IOutboxHandler
{
    public const string MessageType = "billing.cancel";
    public string Type => MessageType;

    public async Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Deserialize<BillingCancelPayload>(message.Payload)
            ?? throw new InvalidOperationException($"Outbox message {message.Id} has an unreadable billing-cancel payload.");

        await provider.CancelSubscriptionAsync(payload.StripeSubscriptionId, cancellationToken);
    }
}
