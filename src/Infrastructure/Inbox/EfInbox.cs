using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Infrastructure.Inbox;

/// <summary>
/// EF implementation of <see cref="IInbox"/>. Claims a delivery with a single
/// <c>INSERT … ON CONFLICT DO NOTHING</c> against the unique <c>(Source, IdempotencyKey)</c> index:
/// the insert affects one row on the first delivery (claimed) and zero on a duplicate. This is
/// race-free by construction — concurrent claims of the same key serialise on the unique index, so
/// exactly one wins — without needing the outbox's <c>SKIP LOCKED</c> queue-claim (that pattern picks
/// work off a queue; this is a dedup gate). Runs on the shared <see cref="AppDbContext"/>, so it
/// enlists in the caller's transaction and commits with the work it guards.
/// </summary>
public sealed class EfInbox(AppDbContext db, TimeProvider clock) : IInbox
{
    public async Task<bool> TryClaimAsync(string source, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var rows = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "InboxMessages" ("Id", "Source", "IdempotencyKey", "ReceivedAt")
            VALUES ({Guid.CreateVersion7()}, {source}, {idempotencyKey}, {clock.GetUtcNow()})
            ON CONFLICT ("Source", "IdempotencyKey") DO NOTHING
            """, cancellationToken);

        return rows == 1;
    }
}
