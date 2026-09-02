namespace Vuelto.Core.Abstractions;

/// <summary>
/// Idempotency gate for inbound at-least-once deliveries (webhooks). Claim a delivery before acting on
/// it; a redelivery of the same key is rejected so the effect happens exactly once (ADR-007).
/// <para>
/// <b>Use inside the caller's transaction.</b> The claim and the work it guards must commit together:
/// wrap <see cref="TryClaimAsync"/> + the processing in an <c>IUnitOfWork</c> transaction and commit
/// only after the work succeeds. If the work throws and the transaction rolls back, the claim rolls
/// back too, so the inevitable redelivery re-processes — that's the correct exactly-once-effect
/// behaviour. (Claiming in autocommit and then doing the work would mark a delivery handled even if the
/// work fails — don't.)
/// </para>
/// </summary>
public interface IInbox
{
    /// <summary>
    /// Records (<paramref name="source"/>, <paramref name="idempotencyKey"/>) if unseen. Returns
    /// <c>true</c> on the first delivery (caller should process it), <c>false</c> if already claimed
    /// (caller should no-op). Race-free across concurrent deliveries via a unique constraint.
    /// </summary>
    Task<bool> TryClaimAsync(string source, string idempotencyKey, CancellationToken cancellationToken = default);
}
