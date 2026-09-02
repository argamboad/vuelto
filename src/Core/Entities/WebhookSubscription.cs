namespace Vuelto.Core.Entities;

/// <summary>
/// A tenant's outbound webhook subscription (HOOKS, ADR-016): "POST me at <see cref="Url"/> when one of
/// <see cref="EventTypes"/> happens." Delivery goes through the transactional outbox (ADR-007), so it's
/// durable + retried. The signing secret is stored <b>encrypted</b> (Data Protection) and revealed once at
/// creation; each delivery is HMAC-signed with it so the receiver can verify authenticity. Tenant-scoped.
/// </summary>
public class WebhookSubscription : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }

    /// <summary>The HTTPS endpoint to POST deliveries to.</summary>
    public required string Url { get; set; }

    /// <summary>Comma-separated event types this endpoint subscribes to (a <c>WebhookEvents</c> value).</summary>
    public required string EventTypes { get; set; }

    /// <summary>The signing secret, encrypted at rest (Data Protection). Never returned after creation.</summary>
    public required string EncryptedSecret { get; set; }

    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Set when the subscription is disabled; a disabled subscription receives no deliveries.</summary>
    public DateTimeOffset? DisabledAt { get; set; }

    public bool IsActive => DisabledAt is null;

    /// <summary>Whether this subscription wants <paramref name="eventType"/> (exact match, case-insensitive).</summary>
    public bool Subscribes(string eventType) =>
        EventTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(t => string.Equals(t, eventType, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Example outbound event types (HOOKS). A feature fires one via <c>IWebhookPublisher.PublishAsync</c>;
/// subscriptions choose which to receive. <c>Ping</c> backs the "send test" action. Replace/extend with
/// the app's real domain events.
/// </summary>
public static class WebhookEvents
{
    public const string Ping = "ping";

    /// <summary>Event types the platform knows about (offered in the management UI / validated on subscribe).</summary>
    public static readonly IReadOnlyList<string> Known = [Ping];
}
