namespace Vuelto.Core.Entities;

/// <summary>
/// A dedup ledger entry for an inbound at-least-once delivery (e.g. a Stripe webhook). Recording a
/// <see cref="Source"/> + <see cref="IdempotencyKey"/> exactly once is what makes inbound processing
/// idempotent: the first delivery claims a row and does the work in the same transaction; a redelivery
/// of the same key finds the row already present and is a no-op (ADR-007).
/// <para>
/// Platform infrastructure — NOT <c>ITenantScoped</c> (an inbound event is reconciled to its tenant by
/// the handler, e.g. via the Stripe customer id).
/// </para>
/// </summary>
public class InboxMessage
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Namespaces the key so ids from different providers can't collide (e.g. <c>"stripe"</c>).</summary>
    public required string Source { get; set; }

    /// <summary>The provider's unique delivery id (e.g. a Stripe event id <c>evt_…</c>). Unique with <see cref="Source"/>.</summary>
    public required string IdempotencyKey { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }
}
