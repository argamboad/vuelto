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
    /// <summary>
    /// A rotated-out token presented again within <see cref="IRefreshTokenSettings.ReuseGraceSeconds"/> of its
    /// rotation while the token that replaced it is still live, and the grace not yet spent on it — a benign
    /// race (two tabs refreshing with the same cookie, a refresh whose response was lost), not theft. The
    /// caller spends the grace (<see cref="IRefreshTokenService.TryConsumeGraceAsync"/>), issues a fresh session
    /// and revokes nothing. Requires a LIVE successor: logout and revoke-all revoke it, so a stale token can
    /// never undo a sign-out. One-shot: a further presentation is <see cref="Reuse"/>. ADR-002 addenda,
    /// 2026-09-18 and 2026-09-28.
    /// </summary>
    RotatedWithinGrace,
}

/// <summary>Outcome of <see cref="IRefreshTokenService.InspectRefreshTokenAsync"/>: a status plus the matched token (null when unknown).</summary>
public record RefreshTokenInspection(RefreshTokenStatus Status, RefreshToken? Token);

public interface IRefreshTokenService
{
    /// <summary>
    /// Issues a refresh token. <paramref name="sessionExpiresAt"/> is the presented token's session end when this
    /// is a rotation (the successor inherits it); null starts a new session, whose end is set from
    /// <see cref="IRefreshTokenSettings.AbsoluteLifetimeDays"/> when that knob is on.
    /// </summary>
    Task<IssuedRefreshToken> IssueRefreshTokenAsync(Guid userId, string ipAddress, string provider,
        DateTimeOffset? sessionExpiresAt = null, CancellationToken cancellationToken = default);
    Task<RefreshToken?> ValidateRefreshTokenAsync(string rawToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Classifies a presented refresh token (valid / expired / unknown / reuse / rotated-within-grace) without
    /// rotating it, so the caller can mount a theft response on replay of a rotated token — or, inside the
    /// reuse grace window, recognise the benign race and issue a fresh session instead. This is the
    /// reuse-aware replacement for <see cref="ValidateRefreshTokenAsync"/> on the refresh path.
    /// </summary>
    Task<RefreshTokenInspection> InspectRefreshTokenAsync(string rawToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rotation: revokes <paramref name="tokenId"/> and links it to <paramref name="replacedByTokenId"/>, stamping
    /// the rotation time from the injected clock — if and only if the token is still live. The only way a token
    /// gets <c>RotatedAt</c>, which is what makes it eligible for the reuse grace window. False when a revoke-all
    /// (logout, erasure, theft response) got to the token between the inspection and this call: the caller
    /// refuses the refresh and its transaction rolls the successor back (v4 T54, TB-AUTH-30). Serialized per user
    /// against <see cref="RevokeAllUserTokensAsync"/> for the rest of the caller's transaction.
    /// </summary>
    Task<bool> TryMarkRotatedAsync(Guid tokenId, Guid replacedByTokenId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Spends the one-shot reuse grace on a token that <see cref="InspectRefreshTokenAsync"/> classified as
    /// <see cref="RefreshTokenStatus.RotatedWithinGrace"/>. Atomic: true when this presentation is the one being
    /// forgiven, false when the grace was already spent — or the successor the inspection saw live has been revoked
    /// since — and the caller then mounts the theft response as for <see cref="RefreshTokenStatus.Reuse"/>. Call it
    /// BEFORE issuing the session, inside the same transaction, so a losing racer never mints one. Serialized per
    /// user against <see cref="RevokeAllUserTokensAsync"/> like the rotation.
    /// </summary>
    Task<bool> TryConsumeGraceAsync(Guid tokenId, CancellationToken cancellationToken = default);

    /// <summary>How many times the grace has been spent across the user's tokens — carried by the Warning the grace path logs.</summary>
    Task<int> CountGraceUsesAsync(Guid userId, CancellationToken cancellationToken = default);

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
    public async Task<IssuedRefreshToken> IssueRefreshTokenAsync(Guid userId, string ipAddress, string provider,
        DateTimeOffset? sessionExpiresAt = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
            ipAddress = "unknown";
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("Provider cannot be empty", nameof(provider));

        var rawToken = tokenGenerator.GenerateToken();
        var tokenHash = tokenHasher.HashToken(rawToken);
        var now = clock.GetUtcNow();

        // A rotation inherits the session's end; a sign-in starts one, with an end only when the absolute
        // lifetime knob is on. The token never outlives the session (v4 T36, decision #2).
        var sessionEnd = sessionExpiresAt
            ?? (settings.AbsoluteLifetimeDays is { } days && days > 0 ? now.AddDays(days) : null);
        var expiresAt = now.AddDays(settings.ExpiryDays);
        if (sessionEnd is { } end && end < expiresAt)
            expiresAt = end;

        var refreshToken = new RefreshToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            TokenHash = tokenHash,
            IssuedAt = now,
            ExpiresAt = expiresAt,
            SessionExpiresAt = sessionEnd,
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
        // A revoked row whose hash is being presented = a rotated-out token replayed → theft, unless it is
        // the benign race the grace window covers (just rotated, successor still live).
        if (token.IsRevoked)
        {
            var status = await IsRotatedWithinGraceAsync(token, cancellationToken)
                ? RefreshTokenStatus.RotatedWithinGrace
                : RefreshTokenStatus.Reuse;
            return new RefreshTokenInspection(status, token);
        }
        if (token.ExpiresAt <= clock.GetUtcNow())
            return new RefreshTokenInspection(RefreshTokenStatus.Expired, token);
        return new RefreshTokenInspection(RefreshTokenStatus.Valid, token);
    }

    // All must hold: the window is on; the token was revoked BY ROTATION (logout/revoke-all never stamp
    // RotatedAt); the grace has not been spent on it yet (one-shot, v4 AUTH-1); the rotation is at most
    // ReuseGraceSeconds old; and the successor is still live — logout and revoke-all revoke the successor,
    // which is what keeps a sign-out final against a stale tab.
    private async Task<bool> IsRotatedWithinGraceAsync(RefreshToken token, CancellationToken cancellationToken)
    {
        if (settings.ReuseGraceSeconds <= 0 || token.RotatedAt is not { } rotatedAt || token.ReplacedByTokenId is not { } successorId)
            return false;
        if (token.GraceUsedAt is not null)
            return false;

        var now = clock.GetUtcNow();
        if (now - rotatedAt > TimeSpan.FromSeconds(settings.ReuseGraceSeconds))
            return false;

        var successor = await repository.GetByIdAsync(successorId, cancellationToken);
        return successor is not null
            && successor.UserId == token.UserId
            && !successor.IsRevoked
            && successor.ExpiresAt > now;
    }

    public Task<bool> TryMarkRotatedAsync(Guid tokenId, Guid replacedByTokenId, CancellationToken cancellationToken = default) =>
        repository.TryMarkRotatedAsync(tokenId, replacedByTokenId, clock.GetUtcNow(), cancellationToken);

    public Task<bool> TryConsumeGraceAsync(Guid tokenId, CancellationToken cancellationToken = default) =>
        repository.TryMarkGraceUsedAsync(tokenId, clock.GetUtcNow(), cancellationToken);

    public Task<int> CountGraceUsesAsync(Guid userId, CancellationToken cancellationToken = default) =>
        repository.CountGraceUsesForUserAsync(userId, cancellationToken);

    public Task RevokeRefreshTokenAsync(Guid tokenId, CancellationToken cancellationToken = default) => repository.RevokeAsync(tokenId, cancellationToken);

    public Task RevokeAllUserTokensAsync(Guid userId, CancellationToken cancellationToken = default) => repository.RevokeAllForUserAsync(userId, cancellationToken);
}
