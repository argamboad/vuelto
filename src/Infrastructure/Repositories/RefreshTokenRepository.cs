using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IRefreshTokenRepository"/>.
/// Encapsulates all refresh token persistence logic.
/// </summary>
public class RefreshTokenRepository(AppDbContext db, TimeProvider clock) : IRefreshTokenRepository
{
    public async Task<RefreshToken> CreateAsync(RefreshToken token, CancellationToken cancellationToken = default)
    {
        db.RefreshTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);
        return token;
    }

    public async Task<RefreshToken?> GetValidTokenByHashAsync(string tokenHash, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        return await db.RefreshTokens.FirstOrDefaultAsync(t =>
            t.TokenHash == tokenHash &&
            !t.IsRevoked &&
            t.ExpiresAt > now, cancellationToken);
    }

    public async Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken cancellationToken = default) =>
        await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, cancellationToken);

    public async Task<RefreshToken?> GetByIdAsync(Guid tokenId, CancellationToken cancellationToken = default) =>
        await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tokenId, cancellationToken);

    public async Task<bool> TryMarkRotatedAsync(Guid tokenId, Guid replacedByTokenId, DateTimeOffset rotatedAt, CancellationToken cancellationToken = default)
    {
        // The rotated token is usually tracked already (the inspection read it): Find gives the tracked copy back
        // without a query, and its UserId keys the lock.
        var token = await db.RefreshTokens.FindAsync([tokenId], cancellationToken);
        if (token is null)
            return false;

        return await UnderChainLockAsync(token.UserId, async () =>
        {
            // Set-based AND conditional on purpose (v4 T54, TB-AUTH-30): the WHERE is what refuses a rotation whose
            // inspection a revoke-all has since made stale — the row is rotated only if nothing revoked it meanwhile.
            var rotated = await db.RefreshTokens
                .Where(t => t.Id == tokenId && !t.IsRevoked)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.IsRevoked, true)
                    .SetProperty(t => t.RotatedAt, rotatedAt)
                    .SetProperty(t => t.ReplacedByTokenId, replacedByTokenId), cancellationToken);
            if (rotated == 1)
            {
                // Mirror the persisted values onto the tracked copy without marking it modified (original = current),
                // so a later in-scope read sees the rotation and the next SaveChanges writes nothing again.
                var entry = db.Entry(token);
                token.IsRevoked = true;
                token.RotatedAt = rotatedAt;
                token.ReplacedByTokenId = replacedByTokenId;
                entry.Property(t => t.IsRevoked).OriginalValue = true;
                entry.Property(t => t.RotatedAt).OriginalValue = rotatedAt;
                entry.Property(t => t.ReplacedByTokenId).OriginalValue = replacedByTokenId;
            }
            return rotated == 1;
        }, cancellationToken);
    }

    public async Task<bool> TryMarkGraceUsedAsync(Guid tokenId, DateTimeOffset usedAt, CancellationToken cancellationToken = default)
    {
        var token = await db.RefreshTokens.FindAsync([tokenId], cancellationToken);
        if (token is null)
            return false;

        return await UnderChainLockAsync(token.UserId, async () =>
        {
            // Set-based AND conditional on purpose: the WHERE is what makes the grace one-shot under two racing
            // replays — exactly one UPDATE matches the null — and what refuses it once a revoke-all has killed the
            // successor the inspection saw live (v4 T54, TB-AUTH-30). The row is not read back through the tracker
            // afterwards (the caller only branches on the result), but the tracked copy is kept honest anyway.
            var stamped = await db.RefreshTokens
                .Where(t => t.Id == tokenId && t.GraceUsedAt == null
                    && db.RefreshTokens.Any(successor => successor.Id == t.ReplacedByTokenId && !successor.IsRevoked))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.GraceUsedAt, usedAt), cancellationToken);
            if (stamped == 1)
            {
                // A copy tracked in this context (an inspection read earlier in the same scope) would otherwise keep
                // a null and mask the stamp from a later in-scope read. Mirror the persisted value without marking
                // the entry modified: original = current, so nothing is written again on the next SaveChanges.
                token.GraceUsedAt = usedAt;
                db.Entry(token).Property(t => t.GraceUsedAt).OriginalValue = usedAt;
            }
            return stamped == 1;
        }, cancellationToken);
    }

    public async Task<int> CountGraceUsesForUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await db.RefreshTokens.CountAsync(t => t.UserId == userId && t.GraceUsedAt != null, cancellationToken);

    public async Task RevokeAsync(Guid tokenId, CancellationToken cancellationToken = default)
    {
        // Load-then-flip (not ExecuteUpdate) on purpose: the just-rotated token is usually already
        // tracked in this context, and reuse detection reads it back via GetByHashAsync (no IsRevoked
        // filter). A set-based update would leave the tracked copy stale (IsRevoked=false) and defeat
        // that check. Bulk revoke below has no such read-back, so it stays set-based.
        var token = await db.RefreshTokens.FindAsync([tokenId], cancellationToken);
        if (token != null)
        {
            token.IsRevoked = true;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public Task RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        UnderChainLockAsync(userId, () => db.RefreshTokens
            .Where(t => t.UserId == userId && !t.IsRevoked)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsRevoked, true), cancellationToken), cancellationToken);

    /// <summary>
    /// Runs <paramref name="write"/> holding the user's chain lock (v4 T54, TB-AUTH-30): a Postgres advisory lock
    /// keyed on the user id, held for the rest of the transaction and released with it. No row is locked, and
    /// nothing waits on it but another writer of the SAME user's chain — the rotation (the conditional mark, the
    /// grace claim) and revoke-all — which is exactly the pair that must not interleave: a revoke-all whose single
    /// UPDATE started before a successor was inserted would otherwise skip it. The lock is only meaningful inside a
    /// transaction, so a caller that has none open gets one around the lock and the write.
    /// </summary>
    private async Task<T> UnderChainLockAsync<T>(Guid userId, Func<Task<T>> write, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            await LockChainAsync(userId, cancellationToken);
            return await write();
        }

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockChainAsync(userId, cancellationToken);
        var result = await write();
        await tx.CommitAsync(cancellationToken);
        return result;
    }

    private Task LockChainAsync(Guid userId, CancellationToken cancellationToken) =>
        // hashtextextended folds the id into the bigint the lock takes; the same id always maps to the same lock.
        db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({userId.ToString()}, 0))", cancellationToken);
}
