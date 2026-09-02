using System.Security.Cryptography;
using Perezosoft.Api.Configuration;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Perezosoft.Api.Services;

public enum OtpStatus { Success, Invalid, Expired, TooManyAttempts }

public record OtpResult(OtpStatus Status, User? User);

/// <summary>
/// Maps the internal <see cref="OtpStatus"/> to the client-facing error code for OTP verify.
/// "No active code" (<see cref="OtpStatus.Expired"/>) and "wrong code"
/// (<see cref="OtpStatus.Invalid"/>) collapse to the SAME <c>invalid_code</c> so a caller can't
/// probe whether an address has an outstanding OTP (CONF-6); the full status stays server-side.
/// </summary>
public static class OtpErrors
{
    public static string ClientCode(OtpStatus status) => status switch
    {
        OtpStatus.TooManyAttempts => "too_many_attempts",
        _ => "invalid_code", // Invalid AND Expired (no active code) are indistinguishable to the client
    };
}

/// <summary>
/// Issues and redeems passwordless credentials: magic-link tokens (long random
/// values delivered as a URL) and OTP codes (short numeric values). Credentials
/// are single-use, time-limited, and stored only as hashes. The account is
/// resolved/created at redemption — issuing never creates an account, so a typo'd
/// or probed email leaves no trace.
/// </summary>
public interface IPasswordlessService
{
    /// <summary>Creates a magic-link token for the email and returns the raw value for the URL.</summary>
    Task<string> IssueMagicLinkTokenAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Validates and consumes a magic-link token, returning the account, or null if invalid.</summary>
    Task<User?> RedeemMagicLinkAsync(string email, string token, CancellationToken cancellationToken = default);

    /// <summary>Creates an OTP code for the email and returns the raw code to be emailed.</summary>
    Task<string> IssueOtpAsync(string email, CancellationToken cancellationToken = default);

    /// <summary>Validates and consumes an OTP code, tracking attempts and locking out on abuse.</summary>
    Task<OtpResult> RedeemOtpAsync(string email, string code, CancellationToken cancellationToken = default);
}

public class PasswordlessService(
    ILoginTokenRepository repository,
    IUserService userService,
    ITokenGenerator tokenGenerator,
    ITokenHasher tokenHasher,
    IPasswordlessSettings settings,
    TimeProvider clock) : IPasswordlessService
{
    public async Task<string> IssueMagicLinkTokenAsync(string email, CancellationToken cancellationToken = default)
    {
        email = Normalize(email);
        await repository.InvalidateActiveAsync(email, LoginTokenPurpose.MagicLink, cancellationToken);

        var raw = tokenGenerator.GenerateToken();
        await repository.AddAsync(NewToken(email, LoginTokenPurpose.MagicLink, raw, settings.MagicLinkLifespanMinutes), cancellationToken);
        return raw;
    }

    public async Task<User?> RedeemMagicLinkAsync(string email, string token, CancellationToken cancellationToken = default)
    {
        email = Normalize(email);
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var hash = tokenHasher.HashToken(token);
        var record = await repository.GetActiveByHashAsync(email, LoginTokenPurpose.MagicLink, hash, cancellationToken);
        if (record is null)
            return null;

        // Atomic claim (LB-AUTH-3): two concurrent redemptions of one link (email-client prefetch, a
        // double-click) both reach here having seen ConsumedAt == null — only the winner may sign in.
        if (!await repository.TryConsumeAsync(record.Id, cancellationToken))
            return null;

        return await userService.GetOrCreateByEmailAsync(email, cancellationToken: cancellationToken);
    }

    public async Task<string> IssueOtpAsync(string email, CancellationToken cancellationToken = default)
    {
        email = Normalize(email);
        await repository.InvalidateActiveAsync(email, LoginTokenPurpose.Otp, cancellationToken);

        var code = GenerateNumericCode(settings.OtpLength);
        await repository.AddAsync(NewToken(email, LoginTokenPurpose.Otp, code, settings.OtpLifespanMinutes), cancellationToken);
        return code;
    }

    public async Task<OtpResult> RedeemOtpAsync(string email, string code, CancellationToken cancellationToken = default)
    {
        email = Normalize(email);
        if (string.IsNullOrWhiteSpace(code))
            return new OtpResult(OtpStatus.Invalid, null);

        // Cumulative, resend-proof lockout: count failed attempts across EVERY code issued to this
        // email in the window, not just the current one. Issuing a fresh code (AttemptCount=0) can't
        // hand the attacker another budget, and the lock holds even against the correct code until
        // the window elapses (CONF-5).
        var windowStart = clock.GetUtcNow().AddMinutes(-settings.OtpLockoutWindowMinutes);
        var failuresInWindow = await repository.CountFailedAttemptsSinceAsync(email, LoginTokenPurpose.Otp, windowStart, cancellationToken);
        if (failuresInWindow >= settings.OtpMaxAttempts)
            return new OtpResult(OtpStatus.TooManyAttempts, null);

        var record = await repository.GetLatestActiveAsync(email, LoginTokenPurpose.Otp, cancellationToken);
        if (record is null)
            return new OtpResult(OtpStatus.Expired, null); // none active → expired or never issued

        // Timing-safe comparison — the OTP code is low-entropy, so don't leak it via
        // early-exit string equality.
        if (tokenHasher.Verify(code, record.CodeHash))
        {
            // Atomic claim (LB-AUTH-3): concurrent submissions of one correct code must mint ONE session.
            if (!await repository.TryConsumeAsync(record.Id, cancellationToken))
                return new OtpResult(OtpStatus.Expired, null); // someone else already redeemed it
            var user = await userService.GetOrCreateByEmailAsync(email, cancellationToken: cancellationToken);
            return new OtpResult(OtpStatus.Success, user);
        }

        // Wrong code — count the attempt atomically, then evaluate the cap against the PERSISTED total
        // (LB-AUTH-2). A read-modify-write here let racing guesses last-writer-wins the increment and slip
        // past the cap — the one backstop that is deliberately IP-independent.
        await repository.IncrementAttemptAsync(record.Id, cancellationToken);
        var totalFailures = await repository.CountFailedAttemptsSinceAsync(email, LoginTokenPurpose.Otp, windowStart, cancellationToken);
        if (totalFailures >= settings.OtpMaxAttempts)
        {
            await repository.TryConsumeAsync(record.Id, cancellationToken); // lock: burn the code
            return new OtpResult(OtpStatus.TooManyAttempts, null);
        }

        return new OtpResult(OtpStatus.Invalid, null);
    }

    private LoginToken NewToken(string email, string purpose, string raw, int lifespanMinutes) => new()
    {
        Id = Guid.CreateVersion7(),
        Email = email,
        CodeHash = tokenHasher.HashToken(raw),
        Purpose = purpose,
        ExpiresAt = clock.GetUtcNow().AddMinutes(lifespanMinutes),
        CreatedAt = clock.GetUtcNow()
    };

    private static string Normalize(string email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    private static string GenerateNumericCode(int length)
    {
        var digits = new char[length];
        for (var i = 0; i < length; i++)
            digits[i] = (char)('0' + RandomNumberGenerator.GetInt32(0, 10));
        return new string(digits);
    }
}
