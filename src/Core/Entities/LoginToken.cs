namespace Vuelto.Core.Entities;

/// <summary>
/// A single-use, time-limited credential for passwordless sign-in — either a
/// magic-link token (long random value emailed as a URL) or an OTP (short numeric
/// code emailed to the user). Only the hash is stored, never the raw value.
/// </summary>
public class LoginToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Normalized (lower-cased) email the credential was issued to.</summary>
    public required string Email { get; set; }

    /// <summary>SHA-256 hash of the raw token/code.</summary>
    public required string CodeHash { get; set; }

    /// <summary><see cref="LoginTokenPurpose"/> — magic link or OTP.</summary>
    public required string Purpose { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set when the credential is redeemed (or locked out); null while usable.</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>Failed verification attempts — used to lock out OTP brute-forcing.</summary>
    public int AttemptCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // Derived, never stored. The *At(now) overloads are the deterministic core (testable with an
    // explicit clock); the parameterless properties are ambient-time conveniences that delegate to
    // them. Production reads filter in SQL, so these are for in-memory checks/tests only.
    public bool IsConsumed => ConsumedAt.HasValue;
    public bool IsExpiredAt(DateTimeOffset now) => now >= ExpiresAt;
    public bool IsValidAt(DateTimeOffset now) => !IsConsumed && !IsExpiredAt(now);

    public bool IsExpired => IsExpiredAt(DateTimeOffset.UtcNow);
    public bool IsValid => IsValidAt(DateTimeOffset.UtcNow);
}

/// <summary>Discriminates the kind of one-time credential.</summary>
public static class LoginTokenPurpose
{
    public const string MagicLink = "magic-link";
    public const string Otp = "otp";
}
