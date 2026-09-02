namespace Vuelto.Core.Abstractions;

/// <summary>
/// Enqueues a side effect onto the transactional outbox. The message is staged on the current
/// unit of work and becomes durable when the caller's transaction commits, so the effect is
/// atomic with the business change that produced it (ADR-007). It does NOT call
/// <c>SaveChanges</c> itself — callers already inside a unit of work get atomicity for free;
/// callers with no ambient transaction (e.g. the email decorator) persist explicitly.
/// <para>
/// Delivery is asynchronous and at-least-once, so every <see cref="IOutboxHandler"/> MUST be
/// idempotent.
/// </para>
/// </summary>
public interface IOutbox
{
    /// <param name="type">Handler discriminator (e.g. <c>"email"</c>).</param>
    /// <param name="payload">Opaque JSON understood by the matching handler.</param>
    /// <param name="tenantId">Optional owning tenant — context for the handler, not a scoping key.</param>
    Task EnqueueAsync(string type, string payload, Guid? tenantId = null, CancellationToken cancellationToken = default);
}
