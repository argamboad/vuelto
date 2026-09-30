using Vuelto.Core.Entities;

namespace Vuelto.Core.Repositories;

/// <summary>
/// Repository abstraction for refresh token persistence.
/// Separates token storage concerns from validation logic.
/// </summary>
public interface IRefreshTokenRepository
{
    Task<RefreshToken> CreateAsync(RefreshToken token, CancellationToken cancellationToken = default);
    Task<RefreshToken?> GetValidTokenByHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks up a token by hash regardless of revoked/expired state — used for reuse detection,
    /// where a hash that matches a *revoked* row means a rotated-out token is being replayed
    /// (token theft), as opposed to a hash that matches nothing (genuinely unknown). Returns null
    /// only when no row has that hash.
    /// </summary>
    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a token by id regardless of state, straight from the database (untracked, so a set-based
    /// revoke earlier in the same context is never masked by a stale tracked copy). Used to check that a
    /// rotated-out token's successor is still live. Null when no row has that id.
    /// </summary>
    Task<RefreshToken?> GetByIdAsync(Guid tokenId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rotation: revokes <paramref name="tokenId"/> and links it to the token that replaced it
    /// (<c>RotatedAt</c> = <paramref name="rotatedAt"/>, <c>ReplacedByTokenId</c> = <paramref name="replacedByTokenId"/>),
    /// if and only if it is still live — one conditional statement, under the user's chain lock (see
    /// <see cref="RevokeAllForUserAsync"/>). False when something revoked it since it was inspected (a logout, an
    /// erasure, the theft response): the caller then refuses the refresh and lets its transaction roll the
    /// successor back (v4 T54, TB-AUTH-30). The only writer of the rotation link — plain revocations go through
    /// <see cref="RevokeAsync"/>.
    /// </summary>
    Task<bool> TryMarkRotatedAsync(Guid tokenId, Guid replacedByTokenId, DateTimeOffset rotatedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Spends the one-shot reuse grace on a rotated-out token: stamps <c>GraceUsedAt</c> = <paramref name="usedAt"/>
    /// if and only if it is still null AND the token's successor is still live, in one conditional statement under
    /// the user's chain lock. True when this call won the stamp; false when the grace had already been spent (by an
    /// earlier presentation, or a racing one) or a revoke-all killed the successor since the inspection, in which
    /// case the caller treats the presentation as reuse.
    /// </summary>
    Task<bool> TryMarkGraceUsedAsync(Guid tokenId, DateTimeOffset usedAt, CancellationToken cancellationToken = default);

    /// <summary>How many of the user's tokens have had the grace spent on them (the per-user count the grace log line carries).</summary>
    Task<int> CountGraceUsesForUserAsync(Guid userId, CancellationToken cancellationToken = default);

    Task RevokeAsync(Guid tokenId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes every live token of the user, serialized against a rotation in flight for the same user: both take
    /// the user's chain lock for the rest of their transaction, so this either goes first (and the rotation's
    /// conditional mark then fails) or waits for the rotation to commit and revokes its successor too. Never a
    /// window in which a successor minted on a stale inspection survives "sign out everywhere".
    /// </summary>
    Task RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
