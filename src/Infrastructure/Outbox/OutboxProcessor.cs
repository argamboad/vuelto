using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Text;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Infrastructure.Outbox;

/// <summary>
/// The testable core of the dispatcher: claims due outbox messages and records each outcome —
/// sent, retry-with-backoff, or dead-lettered after <see cref="OutboxOptions.MaxAttempts"/>.
/// <para>
/// Each message is claimed with Postgres <c>FOR UPDATE SKIP LOCKED</c>, so concurrent pollers never
/// double-claim a row, and the row stays locked for the handler's whole run (ADR-007). The attempt is
/// accounted for AT THE CLAIM (v4 T38, R131/R135): the claim transaction bumps <c>AttemptCount</c> and books
/// <c>NextAttemptAt</c>, and commits before the handler runs — so whatever fails afterwards (the handler, its
/// commit, or the failure bookkeeping itself during a database disconnect) the attempt is on the row and the
/// message is not re-claimed every poll. A row that arrives at the claim with its attempts already spent is
/// dead-lettered there, with a terminal stamp, and its handler is not run again. Kept separate from the
/// <see cref="OutboxDispatcher"/> <c>BackgroundService</c> so it can be unit-tested without timing.
/// </para>
/// <para>
/// A finished row — sent or dead — is stamped with <see cref="OutboxMessage.ProcessedAt"/> and its payload
/// cleared to <see cref="OutboxMessage.ClearedPayload"/> unless its handler keeps it (v4 audit H7, decision #6):
/// the payload is a delivery instruction, and once there is nothing left to deliver, a recipient, a body and
/// an attachment have no reason to stay. <see cref="OutboxRetentionJob"/> deletes finished rows later.
/// </para>
/// </summary>
public sealed class OutboxProcessor(
    AppDbContext db,
    IEnumerable<IOutboxHandler> handlers,
    TimeProvider clock,
    OutboxOptions options,
    ILogger<OutboxProcessor> logger)
{
    private readonly Dictionary<string, IOutboxHandler> _handlers = handlers.ToDictionary(h => h.Type);

    /// <summary>Processes up to <see cref="OutboxOptions.BatchSize"/> due messages; returns how many were handled.</summary>
    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken = default)
    {
        var processed = 0;
        for (var i = 0; i < options.BatchSize; i++)
        {
            if (!await ProcessNextAsync(cancellationToken)) break;
            processed++;
        }
        return processed;
    }

    /// <summary>Claims and processes a single due message. Returns false when nothing is due.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsRelational())
            throw new InvalidOperationException("The outbox requires a relational provider (FOR UPDATE SKIP LOCKED).");

        var now = clock.GetUtcNow();

        // 1. CLAIM — its own short transaction, committed before any work: the oldest due+pending row (skipping
        //    any another poller holds) gets its attempt counted and its next attempt booked. The raw SQL orders
        //    and LIMITs internally (at most one row); materialize with ToList to keep EF from layering its own
        //    order-less First operator on top (a noisy false-positive warning on every poll).
        Guid messageId;
        await using (var claim = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var message = (await db.Set<OutboxMessage>()
                .FromSql($"""
                    SELECT * FROM "OutboxMessages"
                    WHERE "Status" = {OutboxStatus.Pending} AND "NextAttemptAt" <= {now}
                    ORDER BY "CreatedAt"
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                    """)
                .ToListAsync(cancellationToken)).FirstOrDefault();

            if (message is null)
            {
                await claim.RollbackAsync(cancellationToken);
                return false;
            }

            if (message.AttemptCount >= options.MaxAttempts)
            {
                // Its attempts were spent and the last failure's bookkeeping never landed: decide here, with
                // the terminal stamp, and never run the handler again.
                DeadLetter(message, now, "attempts exhausted");
                logger.LogError("Outbox message {Id} ({Type}) dead-lettered at claim: {Attempts} attempt(s) already spent",
                    message.Id, message.Type, message.AttemptCount);
                await db.SaveChangesAsync(cancellationToken);
                await claim.CommitAsync(cancellationToken);
                return true;
            }

            message.AttemptCount++;
            message.NextAttemptAt = now + options.BackoffFor(message.AttemptCount);
            await db.SaveChangesAsync(cancellationToken);
            await claim.CommitAsync(cancellationToken);
            messageId = message.Id;
        }
        db.ChangeTracker.Clear();

        // 2. WORK — lock the row again for the handler's whole run (no other poller can deliver it meanwhile,
        //    however long the handler takes), and commit the result with it.
        Exception failure;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            var message = (await db.Set<OutboxMessage>()
                .FromSql($"""SELECT * FROM "OutboxMessages" WHERE "Id" = {messageId} AND "Status" = {OutboxStatus.Pending} FOR UPDATE SKIP LOCKED""")
                .ToListAsync(cancellationToken)).FirstOrDefault();
            if (message is null)
            {
                await tx.RollbackAsync(cancellationToken); // finished or taken elsewhere between the two steps
                return true;
            }

            try
            {
                if (!_handlers.TryGetValue(message.Type, out var handler))
                    throw new InvalidOperationException($"No IOutboxHandler registered for outbox type '{message.Type}'.");

                await handler.HandleAsync(message, cancellationToken);
                message.Status = OutboxStatus.Sent;
                message.ProcessedAt = now;
                message.LastError = null;
                if (!handler.KeepsPayloadWhenDone)
                    message.Payload = OutboxMessage.ClearedPayload;
                // Persist + commit INSIDE the try (v3 audit LB-BILL-2): if these fail — a transient disconnect,
                // or a handler that staged a constraint-violating row that only faults at SaveChanges — the
                // failure is recorded below; the attempt itself was counted at the claim.
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return true;
            }
            catch (Exception ex)
            {
                await SafeRollbackAsync(tx, cancellationToken); // undo the partial (handler + status) work
                failure = ex;
            }
        } // the failed transaction is disposed here, freeing the connection for a fresh one

        // 3. FAILURE bookkeeping — the error text, and dead-lettering when this was the last attempt or the
        //    failure is permanent. Best-effort: the attempt and the retry are already on the row from the
        //    claim, so if this write fails too (the disconnect is still on) nothing is lost but the message.
        try
        {
            await RecordFailedAttemptAsync(messageId, failure, now, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Outbox message {Id}: the failure bookkeeping could not be written; the attempt was counted at claim time", messageId);
        }
        return true;
    }

    /// <summary>
    /// Records a failed attempt's error and, when the cap is reached or the failure is permanent, dead-letters
    /// the message — in a fresh transaction (LB-BILL-2), re-locking the row <c>FOR UPDATE</c> so it serializes
    /// with other pollers. The attempt count and the retry time were booked at the claim.
    /// </summary>
    private async Task RecordFailedAttemptAsync(Guid messageId, Exception cause, DateTimeOffset now, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear(); // drop the rolled-back Status=Sent staging (and any handler-staged rows)
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var message = (await db.Set<OutboxMessage>()
            .FromSql($"""SELECT * FROM "OutboxMessages" WHERE "Id" = {messageId} FOR UPDATE""")
            .ToListAsync(cancellationToken)).FirstOrDefault();
        if (message is null)
        {
            await tx.RollbackAsync(cancellationToken); // already handled/removed elsewhere — nothing to do
            return;
        }

        // A permanent failure (a refused URL, an unreadable payload) won't change on retry (v4 audit H8).
        if (message.AttemptCount >= options.MaxAttempts || cause is OutboxPermanentFailureException)
        {
            DeadLetter(message, now, cause.Message);
            logger.LogError(cause, "Outbox message {Id} ({Type}) dead-lettered after {Attempts} attempt(s)",
                message.Id, message.Type, message.AttemptCount);
        }
        else
        {
            message.LastError = SafeTruncation.Truncate(cause.Message, 1000);
            logger.LogWarning(cause, "Outbox message {Id} ({Type}) failed (attempt {Attempts}); retrying after backoff",
                message.Id, message.Type, message.AttemptCount);
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    // Terminal: nothing will deliver it now. A type with no registered handler has nobody to keep its payload either.
    private void DeadLetter(OutboxMessage message, DateTimeOffset now, string error)
    {
        message.Status = OutboxStatus.DeadLettered;
        message.ProcessedAt = now; // finished too — retention ages dead rows by it
        message.LastError = SafeTruncation.Truncate(error, 1000);
        if (!_handlers.TryGetValue(message.Type, out var handler) || !handler.KeepsPayloadWhenDone)
            message.Payload = OutboxMessage.ClearedPayload;
    }

    private static async Task SafeRollbackAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, CancellationToken cancellationToken)
    {
        // A failed commit can leave the transaction already completed at the server; a rollback then throws.
        try { await tx.RollbackAsync(cancellationToken); } catch { /* already rolled back / completed */ }
    }

}
