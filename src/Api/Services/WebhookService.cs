using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;
using Vuelto.Infrastructure.Webhooks;

namespace Vuelto.Api.Services;

/// <summary>A freshly created subscription plus its signing secret (shown once, never stored plaintext).</summary>
public sealed record WebhookCreated(WebhookSubscription Subscription, string Secret);

/// <summary>
/// Outcome of a synchronous "send test" (HOOKS-2). <see cref="Delivered"/> is true only on a 2xx;
/// <see cref="StatusCode"/> is the endpoint's HTTP status (null on a transport failure) and
/// <see cref="TransportFailed"/> distinguishes a network/DNS error from an endpoint that answered non-2xx.
/// The full failure detail is never surfaced here — it's kept in the recorded <c>WebhookDelivery</c> (GAP-3).
/// </summary>
public sealed record WebhookTestResult(bool Delivered, int? StatusCode, bool TransportFailed);

/// <summary>
/// Manages a tenant's outbound webhook subscriptions (HOOKS, ADR-016). Runs in the current tenant's scope
/// (owner-gated at the endpoint). Generates a signing secret at creation (returned once; stored encrypted),
/// validates the target URL and event types.
/// </summary>
public interface IWebhookSubscriptionService
{
    Task<WebhookCreated?> CreateAsync(Guid createdByUserId, string url, IEnumerable<string>? eventTypes, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WebhookSubscription>> ListAsync(CancellationToken cancellationToken = default);
    Task<WebhookSubscription?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a synchronous signed <c>ping</c> to the subscription and returns the outcome; null if the
    /// subscription isn't found in the current tenant. Records a <see cref="WebhookDelivery"/> row for the
    /// attempt (success and failure) so the delivery log / replay work in-template — HOOKS-2.
    /// </summary>
    Task<WebhookTestResult?> SendTestAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Recent delivery attempts for a subscription (newest first, current tenant only) — HOOKS-2.</summary>
    Task<IReadOnlyList<WebhookDelivery>> ListDeliveriesAsync(Guid subscriptionId, CancellationToken cancellationToken = default);

    /// <summary>Re-enqueues a past delivery's exact payload for delivery again; false if not found — HOOKS-2.</summary>
    Task<bool> ReplayAsync(Guid deliveryId, CancellationToken cancellationToken = default);
}

public sealed class WebhookSubscriptionService(
    IRepository<WebhookSubscription> subscriptions,
    IRepository<WebhookDelivery> deliveries,
    IOutbox outbox,
    ICurrentTenant currentTenant,
    ITokenGenerator tokenGenerator,
    IWebhookSecretProtector protector,
    IWebhookSender sender,
    IOutboundUrlGuard urlGuard,
    TimeProvider clock) : IWebhookSubscriptionService
{
    public async Task<WebhookCreated?> CreateAsync(Guid createdByUserId, string url, IEnumerable<string>? eventTypes, CancellationToken cancellationToken = default)
    {
        // Reject a malformed / non-https / SSRF-targeting URL up front (GAP-2). The sender re-checks at
        // send time too (DNS rebinding), so this is early feedback, not the only line of defense.
        if (!await urlGuard.IsAllowedAsync(url, cancellationToken))
            return null;

        var types = NormalizeEventTypes(eventTypes);
        if (types is null)
            return null; // event types were provided but none are known — reject, don't subscribe to all

        var secret = "whsec_" + tokenGenerator.GenerateToken();
        var subscription = new WebhookSubscription
        {
            Url = url.Trim(),
            EventTypes = string.Join(',', types),
            EncryptedSecret = protector.Protect(secret),
            CreatedByUserId = createdByUserId,
            CreatedAt = clock.GetUtcNow(),
        };
        await subscriptions.AddAsync(subscription, cancellationToken); // TenantId stamped by the interceptor
        await subscriptions.SaveChangesAsync(cancellationToken);
        return new WebhookCreated(subscription, secret);
    }

    public async Task<IReadOnlyList<WebhookSubscription>> ListAsync(CancellationToken cancellationToken = default) =>
        await subscriptions.Query().OrderByDescending(s => s.CreatedAt).ToListAsync(cancellationToken);

    public Task<WebhookSubscription?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        subscriptions.Query().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var subscription = await subscriptions.Query().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (subscription is null)
            return false;

        subscriptions.Remove(subscription);
        await subscriptions.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<WebhookTestResult?> SendTestAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var subscription = await GetAsync(id, cancellationToken); // tenant-scoped
        if (subscription is null)
            return null;

        var secret = protector.Unprotect(subscription.EncryptedSecret);
        var eventId = Guid.CreateVersion7().ToString();
        var body = JsonSerializer.Serialize(new
        {
            id = eventId,
            type = WebhookEvents.Ping,
            created_at = clock.GetUtcNow(),
            data = new { message = "This is a test event from your app." },
        });

        // Same delivery shape as the async outbox handler (WebhookOutboxHandler): a returned HTTP status vs.
        // a transport error, then record ONE WebhookDelivery row either way so the log / replay are testable.
        int? status = null;
        string? transportError = null;
        try
        {
            status = await sender.SendAsync(subscription.Url, secret, WebhookEvents.Ping, eventId, body, cancellationToken);
        }
        catch (Exception ex)
        {
            transportError = ex.Message; // network/timeout/DNS — no HTTP status
        }

        var success = status is >= 200 and < 300;

        await deliveries.AddAsync(new WebhookDelivery
        {
            TenantId = currentTenant.TenantId ?? subscription.TenantId,
            SubscriptionId = subscription.Id,
            EventType = WebhookEvents.Ping,
            EventId = eventId,
            Body = body,
            Success = success,
            StatusCode = status,
            Error = success ? null : transportError ?? $"HTTP {status}", // kept server-side; never a secret
            CreatedAt = clock.GetUtcNow(),
        }, cancellationToken);
        await deliveries.SaveChangesAsync(cancellationToken);

        // Don't leak internal DNS/connection detail to the tenant (GAP-3): the row keeps the detail, the
        // caller only learns delivered/status and whether the transport failed.
        return new WebhookTestResult(success, status, TransportFailed: transportError is not null);
    }

    public async Task<IReadOnlyList<WebhookDelivery>> ListDeliveriesAsync(Guid subscriptionId, CancellationToken cancellationToken = default)
    {
        // WebhookDelivery isn't ITenantScoped, so filter by tenant explicitly (isolation is by TenantId).
        var tenantId = currentTenant.TenantId ?? Guid.Empty;
        return await deliveries.Query()
            .Where(d => d.TenantId == tenantId && d.SubscriptionId == subscriptionId)
            .OrderByDescending(d => d.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> ReplayAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        var tenantId = currentTenant.TenantId ?? Guid.Empty;
        var delivery = await deliveries.Query()
            .FirstOrDefaultAsync(d => d.Id == deliveryId && d.TenantId == tenantId, cancellationToken);
        if (delivery is null)
            return false;

        // Re-enqueue the SAME payload (subscription + event id + body) so the receiver can dedup on the id.
        var payload = new WebhookOutboxPayload(delivery.SubscriptionId, delivery.EventType, delivery.EventId, delivery.Body);
        await outbox.EnqueueAsync(WebhookOutboxHandler.MessageType, JsonSerializer.Serialize(payload), tenantId, cancellationToken);
        await deliveries.SaveChangesAsync(cancellationToken); // flush the staged outbox message
        return true;
    }

    // A null request defaults to all known event types; a request that names types but none are known is
    // REJECTED (null), never silently subscribed to everything — v2 audit SOLID-3.
    private static IReadOnlyList<string>? NormalizeEventTypes(IEnumerable<string>? eventTypes)
    {
        if (eventTypes is null)
            return WebhookEvents.Known.ToList();

        var requested = eventTypes
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(WebhookEvents.Known.Contains)
            .Distinct()
            .ToList();
        return requested.Count == 0 ? null : requested; // provided but all-invalid → reject
    }
}

/// <summary>
/// The seam a feature calls to emit an outbound event (HOOKS, ADR-016): fans the event out to every active
/// subscription in the current tenant that wants it, enqueuing one durable <c>"webhook"</c> outbox message
/// per subscription (staged on the caller's unit of work, so it commits with the triggering change). The
/// outbox dispatcher then signs + POSTs each, with retry/backoff for free (ADR-007).
/// </summary>
public interface IWebhookPublisher
{
    Task PublishAsync(string eventType, object data, CancellationToken cancellationToken = default);
}

public sealed class WebhookPublisher(
    IRepository<WebhookSubscription> subscriptions,
    IOutbox outbox,
    ICurrentTenant currentTenant,
    TimeProvider clock) : IWebhookPublisher
{
    public async Task PublishAsync(string eventType, object data, CancellationToken cancellationToken = default)
    {
        // Active subscriptions (tenant-scoped); the event-type match is in-memory (stored comma-separated).
        var active = (await subscriptions.Query().Where(s => s.DisabledAt == null).ToListAsync(cancellationToken))
            .Where(s => s.Subscribes(eventType))
            .ToList();
        if (active.Count == 0)
            return;

        var tenantId = currentTenant.TenantId;
        foreach (var subscription in active)
        {
            var eventId = Guid.CreateVersion7().ToString();
            var body = JsonSerializer.Serialize(new
            {
                id = eventId,
                type = eventType,
                created_at = clock.GetUtcNow(),
                data,
            });
            var payload = new WebhookOutboxPayload(subscription.Id, eventType, eventId, body);
            await outbox.EnqueueAsync(WebhookOutboxHandler.MessageType, JsonSerializer.Serialize(payload), tenantId, cancellationToken);
        }
    }
}
