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
    /// (<c>RotatedAt</c> = <paramref name="rotatedAt"/>, <c>ReplacedByTokenId</c> = <paramref name="replacedByTokenId"/>).
    /// The only writer of the rotation link — plain revocations go through <see cref="RevokeAsync"/>.
    /// </summary>
    Task MarkRotatedAsync(Guid tokenId, Guid replacedByTokenId, DateTimeOffset rotatedAt, CancellationToken cancellationToken = default);

    Task RevokeAsync(Guid tokenId, CancellationToken cancellationToken = default);
    Task RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
