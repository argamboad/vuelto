namespace Vuelto.Api.Configuration;

/// <summary>
/// JWT token configuration settings.
/// </summary>
public interface IJwtSettings
{
    string SecretKey { get; }
    string Issuer { get; }
    int ExpiryMinutes { get; }
}

/// <summary>
/// Refresh token configuration settings.
/// </summary>
public interface IRefreshTokenSettings
{
    int ExpiryDays { get; }

    /// <summary>
    /// Reuse grace window (<c>RefreshToken:ReuseGraceSeconds</c>, default 60): a rotated-out token presented
    /// again within this many seconds of its rotation, while its successor is still live, is treated as a
    /// benign race (two tabs, a lost response) and gets a fresh session instead of the theft response.
    /// 0 (or less) disables the window — every replay revokes all sessions. ADR-002 addendum, 2026-09-18.
    /// </summary>
    int ReuseGraceSeconds { get; }

    /// <summary>
    /// Optional absolute session lifetime (<c>RefreshToken:AbsoluteLifetimeDays</c>; null or 0 = off, the
    /// default): the whole rotation chain ends this many days after the sign-in that started it, however often
    /// it is renewed. With the keep-alive an open tab otherwise stays signed in indefinitely. v4 T36, decision #2.
    /// </summary>
    int? AbsoluteLifetimeDays { get; }
}

/// <summary>
/// Application configuration settings.
/// </summary>
public interface IApplicationSettings
{
    /// <summary>Base URL of the Blazor client (used for redirects after the OAuth round-trip).</summary>
    string ClientUrl { get; }

    /// <summary>
    /// Custom URL scheme a native (mobile) client registers for the OAuth callback,
    /// e.g. "vuelto". Empty when unused. Desktop uses loopback HTTP instead, so
    /// this stays empty until the Android slice. Validated as an allowed native
    /// redirect target alongside loopback addresses.
    /// </summary>
    string NativeCallbackScheme { get; }
}

/// <summary>
/// Passwordless (magic-link + OTP) configuration settings.
/// </summary>
public interface IPasswordlessSettings
{
    int MagicLinkLifespanMinutes { get; }
    int OtpLifespanMinutes { get; }
    int OtpLength { get; }

    /// <summary>
    /// Maximum failed OTP guesses allowed per email within <see cref="OtpLockoutWindowMinutes"/>.
    /// Counted CUMULATIVELY across codes, so requesting a fresh code does not reset the budget.
    /// </summary>
    int OtpMaxAttempts { get; }

    /// <summary>Sliding lockout window (minutes) over which failed OTP attempts are summed per email.</summary>
    int OtpLockoutWindowMinutes { get; }
}

/// <summary>
/// Multi-factor step-up brute-force configuration (MFA-2 / v3 audit ADM-3). TOTP has no per-code record
/// to count against, so the cap is per-user consecutive failures on <c>UserMfa</c>.
/// </summary>
public interface IMfaSettings
{
    /// <summary>Consecutive failed step-up verifications that arm a lockout (per user).</summary>
    int MaxAttempts { get; }

    /// <summary>How long (minutes) a user's step-up stays locked once the cap is hit.</summary>
    int LockoutWindowMinutes { get; }
}

/// <summary>
/// Tenant-invitation configuration settings.
/// </summary>
public interface IInvitationSettings
{
    /// <summary>How long an invitation token stays valid, in days.</summary>
    int LifespanDays { get; }
}
