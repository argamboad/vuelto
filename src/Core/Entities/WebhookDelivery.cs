namespace Vuelto.Core.Entities;

/// <summary>
/// A record of one outbound webhook delivery attempt (HOOKS-2, ADR-016) — the tenant-facing debug trail:
/// which event went to which subscription, whether the endpoint accepted it, and the response/error. One
/// row per attempt (retries add rows), and the sent <see cref="Body"/> is kept so a delivery can be
/// **replayed**. Like <c>OutboxMessage</c> it is deliberately <b>not</b> <c>ITenantScoped</c> (it's written
/// from the tenant-less outbox dispatcher); <see cref="TenantId"/> is a plain column the read side filters on.
/// </summary>
public class WebhookDelivery
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Owning tenant — a filter column for the read side, not a global-filter scoping key.</summary>
    public Guid TenantId { get; set; }

    public Guid SubscriptionId { get; set; }
    public required string EventType { get; set; }
    public required string EventId { get; set; }

    /// <summary>The exact JSON payload that was sent — retained so the delivery can be replayed.</summary>
    public required string Body { get; set; }

    /// <summary>True when the endpoint returned a 2xx.</summary>
    public bool Success { get; set; }

    /// <summary>The HTTP status the endpoint returned, or null on a transport failure.</summary>
    public int? StatusCode { get; set; }

    /// <summary>Failure detail (never a secret) when the attempt didn't succeed.</summary>
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
