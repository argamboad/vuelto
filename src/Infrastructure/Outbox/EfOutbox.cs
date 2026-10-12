using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Infrastructure.Outbox;

/// <summary>
/// EF implementation of <see cref="IOutbox"/>. Stages an <see cref="OutboxMessage"/> on the shared
/// request-scoped <see cref="AppDbContext"/> without saving, so it commits in the same transaction
/// as the caller's business change (atomic effects — ADR-007).
/// </summary>
public sealed class EfOutbox(AppDbContext db, TimeProvider clock) : IOutbox
{
    public async Task EnqueueAsync(string type, string payload, Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        await db.Set<OutboxMessage>().AddAsync(new OutboxMessage
        {
            Type = type,
            Payload = payload,
            TenantId = tenantId,
            Status = OutboxStatus.Pending,
            CreatedAt = now,
            NextAttemptAt = now,
        }, cancellationToken);
        // Intentionally no SaveChanges — see IOutbox: persistence rides the caller's commit.
    }
}
