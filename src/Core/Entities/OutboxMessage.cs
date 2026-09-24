namespace Vuelto.Core.Entities;

/// <summary>
/// A durable record of a side effect to perform out-of-band (email, a webhook call, …). It is
/// written in the SAME transaction as the business change that produced it, so the effect is
/// atomic with the data — no "saved the row but lost the email" (ADR-007). The
/// <c>OutboxDispatcher</c> then delivers it via the matching <see cref="Vuelto.Core.Abstractions.IOutboxHandler"/>.
/// <para>
/// Deliberately NOT <c>ITenantScoped</c>: the outbox is platform infrastructure and may carry
/// system (tenant-less) effects, so it is outside the global tenant query filter. The optional
/// <see cref="TenantId"/> names the tenant a row belongs to when there is one: it is how that tenant's
/// dissolve finds the rows whose handler says they dissolve with it (<c>OutboxDataContributor</c>, v4 audit H6).
/// </para>
/// </summary>
public class OutboxMessage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Handler discriminator (e.g. <c>"email"</c>). Exactly one handler claims each type.</summary>
    public required string Type { get; set; }

    /// <summary>Opaque JSON payload understood only by the matching handler.</summary>
    public required string Payload { get; set; }

    /// <summary>Optional owning tenant — not a query-filter key, but the key its dissolve removes rows by.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>One of <see cref="OutboxStatus"/>.</summary>
    public string Status { get; set; } = OutboxStatus.Pending;

    public int AttemptCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Earliest time this message may be dispatched; advanced on each retry (backoff).</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>When the message was successfully delivered (null until then).</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Last failure detail for a retrying/dead-lettered message. Never a secret.</summary>
    public string? LastError { get; set; }
}

/// <summary>Status constants for <see cref="OutboxMessage.Status"/> (string, like the role/purpose constants).</summary>
public static class OutboxStatus
{
    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string DeadLettered = "dead";
}
