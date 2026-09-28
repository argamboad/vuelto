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

    public async Task MarkRotatedAsync(Guid tokenId, Guid replacedByTokenId, DateTimeOffset rotatedAt, CancellationToken cancellationToken = default)
    {
        // Load-then-flip for the same reason as RevokeAsync: the rotated token is tracked in this context.
        var token = await db.RefreshTokens.FindAsync([tokenId], cancellationToken);
        if (token != null)
        {
            token.IsRevoked = true;
            token.RotatedAt = rotatedAt;
            token.ReplacedByTokenId = replacedByTokenId;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<bool> TryMarkGraceUsedAsync(Guid tokenId, DateTimeOffset usedAt, CancellationToken cancellationToken = default)
    {
        // Set-based AND conditional on purpose: the WHERE is what makes the grace one-shot under two racing
        // replays — exactly one UPDATE matches the null. The row is not read back through the tracker
        // afterwards (the caller only branches on the result), so the stale-tracked-copy concern of RevokeAsync
        // does not apply here.
        var stamped = await db.RefreshTokens
            .Where(t => t.Id == tokenId && t.GraceUsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.GraceUsedAt, usedAt), cancellationToken);
        if (stamped == 1 && db.RefreshTokens.Local.FirstOrDefault(t => t.Id == tokenId) is { } tracked)
        {
            // A copy tracked in this context (an inspection read earlier in the same scope) would otherwise keep
            // a null and mask the stamp from a later in-scope read. Mirror the persisted value without marking
            // the entry modified: original = current, so nothing is written again on the next SaveChanges.
            tracked.GraceUsedAt = usedAt;
            db.Entry(tracked).Property(t => t.GraceUsedAt).OriginalValue = usedAt;
        }
        return stamped == 1;
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

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await db.RefreshTokens
            .Where(t => t.UserId == userId && !t.IsRevoked)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsRevoked, true), cancellationToken);
}
