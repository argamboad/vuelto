using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;

namespace Perezosoft.Api.Services;

/// <summary>
/// Mints and reads the short-lived, <b>signed</b> MFA challenge issued after primary auth when a user
/// has MFA enabled (MFA-2, ADR-012). The token binds the user + the original provider + whether the
/// login was native + a single-use id, with an expiry — signed with Data Protection (keys in the DB),
/// so an expired or tampered challenge fails closed. It carries no secret; it only says "this user
/// still owes a second factor for this login". The single-use id is tracked in <see cref="IMemoryCache"/>
/// so a challenge redeems <b>once</b> (v2 audit LOGIC-S1): the caller reads it, verifies the code, then
/// <see cref="Consume"/>s it before issuing the session, so a replay of the same challenge is refused.
/// </summary>
public interface IMfaChallengeService
{
    string Mint(Guid userId, string provider, bool native);
    bool TryRead(string challenge, out Guid userId, out string provider, out bool native, out string challengeId);

    /// <summary>Marks the challenge redeemed; false if already used (replay) or unknown/expired.</summary>
    bool Consume(string challengeId);

    /// <summary>
    /// Hands a claimed challenge back after the second-factor check FAILED, so the user can retry the same
    /// step-up (v3 audit LB-AUTH-1): the caller claims the challenge BEFORE touching the factor, so a lost
    /// claim can never burn a recovery code — but a mere typo must not force a fresh sign-in. Only ever
    /// called when no session was issued. The signed token's own expiry still caps the real lifetime, so
    /// restoring cannot extend a challenge beyond it.
    /// </summary>
    void Restore(string challengeId);
}

public sealed class MfaChallengeService(IDataProtectionProvider dataProtection, IMemoryCache cache) : IMfaChallengeService
{
    private const string CacheKeyPrefix = "mfa-challenge";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly ITimeLimitedDataProtector _protector =
        dataProtection.CreateProtector("Template.Mfa.Challenge.v1").ToTimeLimitedDataProtector();

    public string Mint(Guid userId, string provider, bool native)
    {
        var challengeId = Guid.NewGuid().ToString("N");
        cache.Set(Key(challengeId), true, Lifetime); // valid until consumed or expired
        return _protector.Protect($"{userId:D}|{provider}|{(native ? "1" : "0")}|{challengeId}", Lifetime);
    }

    public bool TryRead(string challenge, out Guid userId, out string provider, out bool native, out string challengeId)
    {
        userId = default;
        provider = "";
        native = false;
        challengeId = "";
        if (string.IsNullOrEmpty(challenge))
            return false;

        try
        {
            var parts = _protector.Unprotect(challenge).Split('|');
            if (parts.Length != 4 || !Guid.TryParse(parts[0], out userId))
                return false;
            provider = parts[1];
            native = parts[2] == "1";
            challengeId = parts[3];
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public bool Consume(string challengeId)
    {
        var key = Key(challengeId);
        if (string.IsNullOrEmpty(challengeId) || !cache.TryGetValue(key, out bool _))
            return false;
        cache.Remove(key); // single-use
        return true;
    }

    public void Restore(string challengeId)
    {
        if (!string.IsNullOrEmpty(challengeId))
            cache.Set(Key(challengeId), true, Lifetime); // the protector's embedded expiry still caps it
    }

    private static string Key(string challengeId) => $"{CacheKeyPrefix}:{challengeId}";
}
