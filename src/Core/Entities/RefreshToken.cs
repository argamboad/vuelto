namespace Vuelto.Core.Entities;

/// <summary>
/// Refresh token for session persistence and token rotation.
/// Stored in the database (hashed) for server-side validation.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool IsRevoked { get; set; }
    public required string IssuedFromIp { get; set; }

    /// <summary>
    /// OAuth provider used at sign-in for this session; carried through
    /// rotation so refreshed JWTs keep an accurate provider claim.
    /// </summary>
    public required string Provider { get; set; }

    /// <summary>
    /// When this token was rotated out (revoked because it was exchanged for <see cref="ReplacedByTokenId"/>).
    /// Set ONLY by rotation — never by logout, revoke-all or any other revocation — so a revoked token with
    /// a null <c>RotatedAt</c> was killed on purpose. Drives the reuse grace window (ADR-002 addendum,
    /// 2026-09-18): the same token presented again shortly after this instant, while its successor is still
    /// live, is a benign race (two tabs, a lost response), not theft.
    /// </summary>
    public DateTimeOffset? RotatedAt { get; set; }

    /// <summary>
    /// The token that replaced this one at rotation (a soft link to another <see cref="RefreshToken"/> row —
    /// no FK, so the hourly expired-token cleanup can delete rows in any order). Set together with
    /// <see cref="RotatedAt"/> and only by rotation. The grace window requires this successor to be live
    /// (not revoked, not expired): logout and revoke-all revoke it, so a stale token can never undo them.
    /// </summary>
    public Guid? ReplacedByTokenId { get; set; }

    /// <summary>
    /// When the reuse grace was spent on this rotated-out token: the one presentation inside the window that
    /// was forgiven as a benign race and answered with a fresh session. The grace is one-shot (v4 AUTH-1,
    /// R81): once stamped, any further presentation of this token is reuse, whatever the clock says. Set only
    /// by the refresh path, atomically, so two racing replays cannot both be forgiven.
    /// </summary>
    public DateTimeOffset? GraceUsedAt { get; set; }

    /// <summary>
    /// When the whole session ends, if the deployment sets <c>RefreshToken:AbsoluteLifetimeDays</c> (v4 T36):
    /// stamped at sign-in, inherited by every successor at rotation, and a ceiling on <see cref="ExpiresAt"/> —
    /// so a chain renewed forever by the keep-alive still ends. Null when the knob is off (the default).
    /// </summary>
    public DateTimeOffset? SessionExpiresAt { get; set; }
}
