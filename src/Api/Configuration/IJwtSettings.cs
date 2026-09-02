namespace Perezosoft.Api.Configuration;

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
    /// e.g. "perezosoft". Empty when unused. Desktop uses loopback HTTP instead, so
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
