using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Infrastructure.Outbox;

/// <summary>
/// Deletes finished outbox rows once they are older than <see cref="OutboxOptions.RetentionDays"/> (v4 audit H7,
/// decision #6). The processor already cleared their payloads; this bounds the table. Rows finished before either
/// change are caught up here: a sent or dead row that still carries a payload is cleared, and a dead row without
/// a <see cref="OutboxMessage.ProcessedAt"/> is aged by when it was created. Pending rows are never touched, and
/// a type whose handler keeps its payload as a record (<see cref="IOutboxHandler.KeepsPayloadWhenDone"/>) is
/// neither cleared nor deleted.
/// </summary>
public sealed class OutboxRetentionJob(
    AppDbContext db,
    IEnumerable<IOutboxHandler> handlers,
    OutboxOptions options,
    TimeProvider clock) : IScheduledJob
{
    private readonly string[] _kept = [.. handlers.Where(h => h.KeepsPayloadWhenDone).Select(h => h.Type)];

    public string Name => "outbox-retention";
    public TimeSpan Interval => TimeSpan.FromHours(6);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = clock.GetUtcNow().AddDays(-options.RetentionDays);
        var finished = db.OutboxMessages.Where(m =>
            (m.Status == OutboxStatus.Sent || m.Status == OutboxStatus.DeadLettered) && !_kept.Contains(m.Type));

        await finished
            .Where(m => (m.ProcessedAt ?? m.CreatedAt) < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        await finished
            .Where(m => m.Payload != OutboxMessage.ClearedPayload)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Payload, OutboxMessage.ClearedPayload), cancellationToken);
    }
}
