using Vuelto.Core.Entities;

namespace Vuelto.Core.Abstractions;

/// <summary>
/// Handles outbox messages of a single <see cref="Type"/>. The dispatcher resolves all registered
/// handlers and routes each message to the one whose <see cref="Type"/> matches.
/// <para>
/// MUST be idempotent: delivery is at-least-once, so the same message may be handed to
/// <see cref="HandleAsync"/> more than once (e.g. after a crash between send and commit). Throw to
/// signal failure — the dispatcher will retry with backoff and dead-letter after the attempt cap.
/// </para>
/// </summary>
public interface IOutboxHandler
{
    /// <summary>The <see cref="OutboxMessage.Type"/> this handler claims.</summary>
    string Type { get; }

    Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken = default);
}
