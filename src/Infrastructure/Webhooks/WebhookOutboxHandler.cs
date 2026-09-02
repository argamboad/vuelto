using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Entities;
using Perezosoft.Infrastructure.Persistence;

namespace Perezosoft.Infrastructure.Webhooks;

/// <summary>The outbox payload for a single webhook delivery (one per subscription × event).</summary>
public sealed record WebhookOutboxPayload(Guid SubscriptionId, string EventType, string EventId, string Body);

/// <summary>
/// Outbox handler for <c>"webhook"</c> messages (HOOKS, ADR-016): delivers one signed POST to the target
/// subscription. A non-2xx (or transport error) throws, so the outbox retries with backoff and
/// dead-letters after the cap — no bespoke retry logic. If the subscription was removed or disabled since
/// the event was enqueued, the delivery is a no-op (treated as done, not retried). Idempotent: a duplicate
/// dispatch just re-POSTs, which the receiver dedups by the <c>X-Webhook-Id</c> header.
/// </summary>
public sealed class WebhookOutboxHandler(
    AppDbContext db,
    IWebhookSender sender,
    IWebhookSecretProtector protector,
    TimeProvider clock,
    IDbContextFactory<AppDbContext> dbFactory) : IOutboxHandler
{
    public const string MessageType = "webhook";
    public string Type => MessageType;

    public async Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Deserialize<WebhookOutboxPayload>(message.Payload)
            ?? throw new InvalidOperationException($"Outbox message {message.Id} has an unreadable webhook payload.");

        // The outbox is tenant-less, so bypass the tenant filter to load the target subscription by id.
        // (The RLS tag is belt-and-braces: the tenant-less system context already bypasses — ADR-020.)
        var subscription = await db.Set<WebhookSubscription>().IgnoreQueryFilters().TagWith(RlsTags.CrossTenant)
            .FirstOrDefaultAsync(s => s.Id == payload.SubscriptionId, cancellationToken);
        if (subscription is null || !subscription.IsActive)
            return; // removed/disabled since enqueue — nothing to deliver, don't retry

        var secret = protector.Unprotect(subscription.EncryptedSecret);

        int? status = null;
        string? transportError = null;
        try
        {
            status = await sender.SendAsync(subscription.Url, secret, payload.EventType, payload.EventId, payload.Body, cancellationToken);
        }
        catch (Exception ex)
        {
            transportError = ex.Message; // network/timeout — no HTTP status
        }

        var success = status is >= 200 and < 300;

        // Record the attempt (HOOKS-2). Two paths, deliberately different:
        //  - SUCCESS: staged on the shared context, so the row commits atomically with the message's
        //    Sent flip — the log never shows a success the outbox didn't record.
        //  - FAILURE: written through a FRESH out-of-band context (own connection, own SaveChanges)
        //    BEFORE the throw. The OutboxProcessor rolls the ambient transaction back on failure and
        //    clears the tracker, so a staged row would be silently discarded — which is exactly what
        //    happened until 2026-08-24: the delivery log only ever recorded successes, leaving an
        //    operator blind precisely when an endpoint was failing.
        var delivery = new WebhookDelivery
        {
            TenantId = message.TenantId ?? subscription.TenantId,
            SubscriptionId = subscription.Id,
            EventType = payload.EventType,
            EventId = payload.EventId,
            Body = payload.Body,
            Success = success,
            StatusCode = status,
            Error = success ? null : transportError ?? $"HTTP {status}",
            CreatedAt = clock.GetUtcNow(),
        };

        if (success)
        {
            db.Set<WebhookDelivery>().Add(delivery);
            return;
        }

        await using (var auditDb = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            auditDb.Set<WebhookDelivery>().Add(delivery);
            await auditDb.SaveChangesAsync(cancellationToken);
        }

        throw new InvalidOperationException(
            transportError ?? $"Webhook delivery to {subscription.Url} returned HTTP {status}."); // → outbox retry
    }
}
