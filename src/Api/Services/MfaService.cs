using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using OtpNet;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Services;

/// <summary>The provisioning URI + secret returned once when enrollment begins (MFA-1, ADR-012).</summary>
public sealed record MfaEnrollment(string ProvisioningUri, string Secret);

public enum MfaConfirmResult { Enabled, InvalidCode, NotEnrolled }
public enum MfaDisableResult { Disabled, InvalidCode, NotEnabled }

/// <summary>
/// Authenticator-app TOTP MFA (MFA-1, ADR-012). The secret is stored <b>encrypted</b> (Data
/// Protection) and only ever leaves as the enrollment provisioning URI; recovery codes are
/// <b>hashed + single-use</b> (<see cref="ITokenHasher"/>). Enable/disable both require a valid code
/// (prove possession). <see cref="VerifyAsync"/> is the check the login step-up (MFA-2) calls.
/// </summary>
public interface IMfaService
{
    Task<bool> IsEnabledAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Starts enrollment (not yet enabled). Null if the user doesn't exist.</summary>
    Task<MfaEnrollment?> BeginEnrollmentAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Confirms enrollment with a code → enables MFA and returns fresh one-time recovery codes.</summary>
    Task<(MfaConfirmResult Result, IReadOnlyList<string> RecoveryCodes)> ConfirmEnrollmentAsync(Guid userId, string code, CancellationToken cancellationToken = default);

    /// <summary>Disables MFA (requires a valid code) and wipes the secret + recovery codes.</summary>
    Task<MfaDisableResult> DisableAsync(Guid userId, string code, CancellationToken cancellationToken = default);

    /// <summary>
    /// Staff recovery path (ADR-021 addendum): wipes the secret + recovery codes <b>without a code</b> —
    /// the possession proof <see cref="DisableAsync"/> demands is exactly what a user who lost both the
    /// authenticator and the codes cannot provide. Only the staff-gated admin endpoint may call this
    /// (identity is verified out-of-band); never expose it on a user-facing path. False when there was
    /// nothing to wipe.
    /// </summary>
    Task<bool> ResetAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>True if the code is a valid TOTP or an unused recovery code (which it then consumes).</summary>
    Task<bool> VerifyAsync(Guid userId, string code, CancellationToken cancellationToken = default);
}

public sealed class MfaService(
    IRepository<UserMfa> mfa,
    IRepository<MfaRecoveryCode> recoveryCodes,
    IUserRepository users,
    IDataProtectionProvider dataProtection,
    IRecoveryCodeHasher recoveryHasher,
    Configuration.IMfaSettings mfaSettings,
    TimeProvider clock) : IMfaService
{
    private const string Issuer = "Vuelto"; // rebrandable — appears in the authenticator app
    private const int RecoveryCodeCount = 10;
    // Recovery codes are HUMAN-entered fallbacks, so they use a short, unambiguous alphabet (no
    // 0/O/1/I/L) formatted as two 5-char groups (e.g. "k7m2q-9xr4t"). NOT the 88-char opaque token
    // generator — that overflowed the maxlength-14 code inputs so the codes could never be entered.
    // Matched case- and separator-insensitively (CanonicalizeRecoveryCode); stored only as the hash
    // of that canonical form.
    private const string RecoveryAlphabet = "23456789abcdefghjkmnpqrstuvwxyz"; // 31 chars, ambiguity removed
    private const int RecoveryCodeLength = 10; // ~49 bits/code; single-use, hashed, and rate-limited
    private static readonly VerificationWindow Window = new(previous: 1, future: 1); // ±1 step for clock skew
    // Purpose string feeds DataProtection key derivation — renaming it makes MFA secrets already
    // encrypted at rest undecryptable. Kept through the Vuelto rename; bump only with a re-encrypt migration.
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Template.Mfa.Secret.v1");

    public async Task<bool> IsEnabledAsync(Guid userId, CancellationToken cancellationToken = default) =>
        await mfa.Query().AnyAsync(m => m.UserId == userId && m.Enabled, cancellationToken);

    public async Task<MfaEnrollment?> BeginEnrollmentAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await users.GetByIdAsync(userId, cancellationToken);
        if (user is null)
            return null;

        // Fresh start: clear any prior (unconfirmed or superseded) state for this user.
        await recoveryCodes.Query().Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await mfa.Query().Where(m => m.UserId == userId).ExecuteDeleteAsync(cancellationToken);

        var secret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
        await mfa.AddAsync(new UserMfa
        {
            UserId = userId,
            EncryptedSecret = _protector.Protect(secret),
            Enabled = false,
            EnrolledAt = clock.GetUtcNow(),
        }, cancellationToken);
        await mfa.SaveChangesAsync(cancellationToken);

        var label = Uri.EscapeDataString($"{Issuer}:{user.Email}");
        var issuer = Uri.EscapeDataString(Issuer);
        var uri = $"otpauth://totp/{label}?secret={secret}&issuer={issuer}&digits=6&period=30";
        return new MfaEnrollment(uri, secret);
    }

    public async Task<(MfaConfirmResult, IReadOnlyList<string>)> ConfirmEnrollmentAsync(Guid userId, string code, CancellationToken cancellationToken = default)
    {
        var record = await mfa.Query().FirstOrDefaultAsync(m => m.UserId == userId, cancellationToken);
        if (record is null)
            return (MfaConfirmResult.NotEnrolled, []);
        if (!TryVerifyTotp(record, code, out _)) // enrollment proof — not a login, so no anti-replay step recorded
            return (MfaConfirmResult.InvalidCode, []);

        record.Enabled = true;
        mfa.Update(record);

        // Issue a fresh recovery-code set (raw shown once; only hashes persisted).
        await recoveryCodes.Query().Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        var raw = new List<string>(RecoveryCodeCount);
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var codeRaw = GenerateRecoveryCode();
            raw.Add(codeRaw);
            await recoveryCodes.AddAsync(new MfaRecoveryCode { UserId = userId, CodeHash = recoveryHasher.Hash(CanonicalizeRecoveryCode(codeRaw)) }, cancellationToken);
        }
        await mfa.SaveChangesAsync(cancellationToken);
        return (MfaConfirmResult.Enabled, raw);
    }

    public async Task<MfaDisableResult> DisableAsync(Guid userId, string code, CancellationToken cancellationToken = default)
    {
        var enabled = await mfa.Query().AnyAsync(m => m.UserId == userId && m.Enabled, cancellationToken);
        if (!enabled)
            return MfaDisableResult.NotEnabled;
        if (!await VerifyAsync(userId, code, cancellationToken))
            return MfaDisableResult.InvalidCode;

        await recoveryCodes.Query().Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await mfa.Query().Where(m => m.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        return MfaDisableResult.Disabled;
    }

    public async Task<bool> ResetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var codesWiped = await recoveryCodes.Query().Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        var mfaWiped = await mfa.Query().Where(m => m.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        return codesWiped + mfaWiped > 0;
    }

    public async Task<bool> VerifyAsync(Guid userId, string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;

        var record = await mfa.Query().FirstOrDefaultAsync(m => m.UserId == userId && m.Enabled, cancellationToken);
        if (record is null)
            return false;

        var now = clock.GetUtcNow();
        // ADM-3 brute-force cap: while locked out, reject WITHOUT consuming an attempt (so a legitimate user
        // isn't held locked indefinitely by an attacker's continued spraying) and without leaking that the
        // account is locked — the endpoint returns the same generic failure as any wrong code (no oracle).
        if (record.LockedUntil is { } until && until > now)
            return false;

        if (TryVerifyTotp(record, code, out var timeStep)
            // Anti-replay (v2 audit LOGIC-S1): a TOTP is valid for a ~90s window (±1 step), so the same
            // code must not mint more than one session. Reject a step already accepted (or an older one).
            && !(record.LastVerifiedTimeStep is { } last && timeStep <= last))
        {
            // The record is tracked (loaded above), so the anti-replay step + the lockout reset persist
            // together in one SaveChanges.
            record.LastVerifiedTimeStep = timeStep;
            record.FailedAttemptCount = 0;
            record.LockedUntil = null;
            mfa.Update(record);
            await mfa.SaveChangesAsync(cancellationToken);
            return true;
        }

        // Otherwise try a single-use recovery code (case- and separator-insensitive). HMAC is deterministic,
        // so the by-hash lookup still works — just under the peppered key (ADM-4).
        var hash = recoveryHasher.Hash(CanonicalizeRecoveryCode(code));
        var recovery = await recoveryCodes.Query()
            .FirstOrDefaultAsync(c => c.UserId == userId && c.CodeHash == hash && c.UsedAt == null, cancellationToken);
        if (recovery is null)
        {
            await RegisterFailedAttemptAsync(userId, now, cancellationToken); // both TOTP and recovery-code miss
            return false;
        }

        recovery.UsedAt = now;
        recoveryCodes.Update(recovery);
        await recoveryCodes.SaveChangesAsync(cancellationToken);
        await ResetLockoutAsync(userId, cancellationToken); // a correct recovery code clears the cap too
        return true;
    }

    /// <summary>
    /// Atomically records a failed step-up and arms a lockout once the configured cap is reached (ADM-3).
    /// A single conditional UPDATE so concurrent sprays can't slip past a read-modify-write race, and the
    /// cap is evaluated on the persisted post-increment value.
    /// </summary>
    private async Task RegisterFailedAttemptAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var lockUntil = now.AddMinutes(mfaSettings.LockoutWindowMinutes);
        var max = mfaSettings.MaxAttempts;
        await mfa.Query()
            .Where(m => m.UserId == userId && m.Enabled)
            .ExecuteUpdateAsync(s => s
                // Hitting the cap arms the lockout and resets the counter to 0 (fresh budget after it lifts);
                // otherwise just increment.
                .SetProperty(m => m.LockedUntil, m => m.FailedAttemptCount + 1 >= max ? lockUntil : m.LockedUntil)
                .SetProperty(m => m.FailedAttemptCount, m => m.FailedAttemptCount + 1 >= max ? 0 : m.FailedAttemptCount + 1),
                cancellationToken);
    }

    /// <summary>Clears the failure counter + any lockout for a user after a successful verification.</summary>
    private Task ResetLockoutAsync(Guid userId, CancellationToken cancellationToken) =>
        mfa.Query()
            .Where(m => m.UserId == userId && (m.FailedAttemptCount != 0 || m.LockedUntil != null))
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.FailedAttemptCount, 0)
                .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null),
                cancellationToken);

    /// <summary>True if <paramref name="code"/> is a valid TOTP; <paramref name="timeStep"/> is the matched step.</summary>
    private bool TryVerifyTotp(UserMfa record, string code, out long timeStep)
    {
        timeStep = 0;
        string secret;
        try { secret = _protector.Unprotect(record.EncryptedSecret); }
        catch (CryptographicException) { return false; }

        return new Totp(Base32Encoding.ToBytes(secret)).VerifyTotp(code.Trim(), out timeStep, Window);
    }

    /// <summary>A short, human-typeable one-time recovery code: two 5-char groups drawn from an
    /// unambiguous alphabet, e.g. <c>k7m2q-9xr4t</c>. <see cref="RandomNumberGenerator.GetInt32(int)"/>
    /// is unbiased across the (non-power-of-two) alphabet.</summary>
    private static string GenerateRecoveryCode()
    {
        var chars = new char[RecoveryCodeLength];
        for (var i = 0; i < RecoveryCodeLength; i++)
            chars[i] = RecoveryAlphabet[RandomNumberGenerator.GetInt32(RecoveryAlphabet.Length)];
        return $"{new string(chars, 0, 5)}-{new string(chars, 5, 5)}";
    }

    /// <summary>Canonical form for hashing and lookup: letters and digits only, lower-cased — so a code
    /// entered with or without the hyphen, in any case, matches the hash stored at enrollment.</summary>
    private static string CanonicalizeRecoveryCode(string code)
    {
        var sb = new System.Text.StringBuilder(code.Length);
        foreach (var ch in code)
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
}
