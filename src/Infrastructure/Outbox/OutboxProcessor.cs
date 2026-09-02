using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Entities;
using Perezosoft.Infrastructure.Persistence;

namespace Perezosoft.Infrastructure.Outbox;

/// <summary>
/// The testable core of the dispatcher: claims due outbox messages and records each outcome —
/// sent, retry-with-backoff, or dead-lettered after <see cref="OutboxOptions.MaxAttempts"/>.
/// <para>
/// Each message is claimed in its own transaction with Postgres <c>FOR UPDATE SKIP LOCKED</c>, so
/// concurrent pollers never double-claim a row and the lock is scoped to a single handler
/// invocation (ADR-007). Kept separate from the <see cref="OutboxDispatcher"/>
/// <c>BackgroundService</c> so it can be unit-tested without timing.
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

        Guid failedMessageId;
        Exception failure;
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            // Claim the oldest due+pending row, skipping any another poller already holds. The raw SQL
            // orders and LIMITs internally (so at most one row returns); materialize with ToList to keep
            // EF from layering its own order-less First operator on top — which only logs a noisy,
            // false-positive "FirstOrDefault without OrderBy" warning on every poll.
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
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            try
            {
                if (!_handlers.TryGetValue(message.Type, out var handler))
                    throw new InvalidOperationException($"No IOutboxHandler registered for outbox type '{message.Type}'.");

                await handler.HandleAsync(message, cancellationToken);
                message.Status = OutboxStatus.Sent;
                message.ProcessedAt = now;
                message.LastError = null;
                // Persist + commit INSIDE the try (v3 audit LB-BILL-2): if these fail — a transient
                // disconnect, or a handler that staged a constraint-violating row that only faults at
                // SaveChanges — the attempt must still be accounted for below, or the message stays Pending,
                // re-runs the side effect every pass, and NEVER dead-letters (a poison-at-commit loop).
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return true;
            }
            catch (Exception ex)
            {
                await SafeRollbackAsync(tx, cancellationToken); // undo the partial (handler + status) work
                failedMessageId = message.Id;
                failure = ex;
            }
        } // the failed transaction is disposed here, freeing the connection for a fresh one

        // Record the failed attempt in its OWN transaction so the bookkeeping survives the rollback above.
        await RecordFailedAttemptAsync(failedMessageId, failure, now, cancellationToken);
        return true;
    }

    /// <summary>
    /// Advances attempt/dead-letter bookkeeping for a message whose processing failed, in a fresh
    /// transaction (LB-BILL-2). Re-claims the row <c>FOR UPDATE</c> so it serializes with other pollers,
    /// then either schedules a backoff retry or dead-letters once the cap is reached.
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

        message.AttemptCount++;
        message.LastError = Truncate(cause.Message, 1000);
        if (message.AttemptCount >= options.MaxAttempts)
        {
            message.Status = OutboxStatus.DeadLettered;
            logger.LogError(cause, "Outbox message {Id} ({Type}) dead-lettered after {Attempts} attempt(s)",
                message.Id, message.Type, message.AttemptCount);
        }
        else
        {
            message.NextAttemptAt = now + Backoff(message.AttemptCount);
            logger.LogWarning(cause, "Outbox message {Id} ({Type}) failed (attempt {Attempts}); retrying after backoff",
                message.Id, message.Type, message.AttemptCount);
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    private static async Task SafeRollbackAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, CancellationToken cancellationToken)
    {
        // A failed commit can leave the transaction already completed at the server; a rollback then throws.
        try { await tx.RollbackAsync(cancellationToken); } catch { /* already rolled back / completed */ }
    }

    private TimeSpan Backoff(int attempt) => options.BackoffBase * Math.Pow(2, attempt - 1);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
