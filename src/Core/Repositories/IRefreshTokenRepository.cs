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
    Task RevokeAsync(Guid tokenId, CancellationToken cancellationToken = default);
    Task RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken = default);
}
