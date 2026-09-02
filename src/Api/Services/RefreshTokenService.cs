using Vuelto.Api.Configuration;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Services;

/// <summary>
/// Result of issuing a refresh token. The raw token goes to the client cookie;
/// only its hash is persisted, so a database leak cannot be used to forge cookies.
/// </summary>
public record IssuedRefreshToken(string RawToken, RefreshToken Token);

/// <summary>Why a presented refresh token was accepted or rejected — see <see cref="RefreshTokenInspection"/>.</summary>
public enum RefreshTokenStatus
{
    /// <summary>Active, unexpired token — safe to rotate.</summary>
    Valid,
    /// <summary>Known token that is past its expiry. Reject (no theft signal).</summary>
    Expired,
    /// <summary>No token with this hash exists. Reject as a plain bad token.</summary>
    Unknown,
    /// <summary>
    /// A previously-rotated (revoked) token is being replayed — a token-theft signal. The caller
    /// should revoke the user's sessions and audit-log it (but must not reveal the distinction to
    /// the client; reuse and unknown return the same response).
    /// </summary>
    Reuse,
}

/// <summary>Outcome of <see cref="IRefreshTokenService.InspectRefreshTokenAsync"/>: a status plus the matched token (null when unknown).</summary>
public record RefreshTokenInspection(RefreshTokenStatus Status, RefreshToken? Token);

public interface IRefreshTokenService
{
    Task<IssuedRefreshToken> IssueRefreshTokenAsync(Guid userId, string ipAddress, string provider, CancellationToken cancellationToken = default);
    Task<RefreshToken?> ValidateRefreshTokenAsync(string rawToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Classifies a presented refresh token (valid / expired / unknown / reuse) without rotating it,
    /// so the caller can mount a theft response on replay of a rotated token. This is the
    /// reuse-aware replacement for <see cref="ValidateRefreshTokenAsync"/> on the refresh path.
    /// </summary>
    Task<RefreshTokenInspection> InspectRefreshTokenAsync(string rawToken, CancellationToken cancellationToken = default);

    Task RevokeRefreshTokenAsync(Guid tokenId, CancellationToken cancellationToken = default);
    Task RevokeAllUserTokensAsync(Guid userId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Manages refresh token lifecycle. Delegates generation to ITokenGenerator,
/// hashing to ITokenHasher, and persistence to IRefreshTokenRepository.
/// </summary>
public class RefreshTokenService(
    IRefreshTokenRepository repository,
    ITokenGenerator tokenGenerator,
    ITokenHasher tokenHasher,
    IRefreshTokenSettings settings,
    TimeProvider clock) : IRefreshTokenService
{
    public async Task<IssuedRefreshToken> IssueRefreshTokenAsync(Guid userId, string ipAddress, string provider, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
            ipAddress = "unknown";
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("Provider cannot be empty", nameof(provider));

        var rawToken = tokenGenerator.GenerateToken();
        var tokenHash = tokenHasher.HashToken(rawToken);
        var now = clock.GetUtcNow();

        var refreshToken = new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            TokenHash = tokenHash,
            IssuedAt = now,
            ExpiresAt = now.AddDays(settings.ExpiryDays),
            IsRevoked = false,
            IssuedFromIp = ipAddress,
            Provider = provider
        };

        var created = await repository.CreateAsync(refreshToken, cancellationToken);
        return new IssuedRefreshToken(rawToken, created);
    }

    public async Task<RefreshToken?> ValidateRefreshTokenAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(rawToken))
            return null;

        var tokenHash = tokenHasher.HashToken(rawToken);
        return await repository.GetValidTokenByHashAsync(tokenHash, cancellationToken);
    }

    public async Task<RefreshTokenInspection> InspectRefreshTokenAsync(string rawToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(rawToken))
            return new RefreshTokenInspection(RefreshTokenStatus.Unknown, null);

        var tokenHash = tokenHasher.HashToken(rawToken);
        var token = await repository.GetByHashAsync(tokenHash, cancellationToken);

        if (token is null)
            return new RefreshTokenInspection(RefreshTokenStatus.Unknown, null);
        // A revoked row whose hash is being presented = a rotated-out token replayed → theft.
        if (token.IsRevoked)
            return new RefreshTokenInspection(RefreshTokenStatus.Reuse, token);
        if (token.ExpiresAt <= clock.GetUtcNow())
            return new RefreshTokenInspection(RefreshTokenStatus.Expired, token);
        return new RefreshTokenInspection(RefreshTokenStatus.Valid, token);
    }

    public Task RevokeRefreshTokenAsync(Guid tokenId, CancellationToken cancellationToken = default) => repository.RevokeAsync(tokenId, cancellationToken);

    public Task RevokeAllUserTokensAsync(Guid userId, CancellationToken cancellationToken = default) => repository.RevokeAllForUserAsync(userId, cancellationToken);
}
