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
