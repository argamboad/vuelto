using System.Security.Cryptography;
using System.Text;
using Perezosoft.Api.Configuration;

namespace Perezosoft.Api.Services;

/// <summary>
/// Keyed hashing for MFA recovery codes (v3 audit ADM-4). Recovery codes are low-entropy (~49.5 bits,
/// deliberately typeable), so a plain unsalted hash of a leaked <c>MfaRecoveryCode</c> row is offline-
/// crackable in day-scale on a GPU → a working second factor. Deterministic so lookups stay by-hash.
/// </summary>
public interface IRecoveryCodeHasher
{
    /// <summary>Peppered hash of a canonicalized recovery code (deterministic — same code ⇒ same hash).</summary>
    string Hash(string canonicalCode);

    /// <summary>Constant-time check of a canonicalized code against a stored hash.</summary>
    bool Verify(string canonicalCode, string storedHash);
}

/// <summary>
/// HMAC-SHA256 under a server PEPPER. The pepper is a key the database does not contain, so a leaked row is
/// useless without it — an attacker would also need the app's <c>Jwt:Secret</c>. The MAC key is DERIVED from
/// that secret via HKDF with a domain-separation label, so it needs no separate config (it inherits
/// <c>Jwt:Secret</c>'s presence + min-length guard) yet stays cryptographically independent of JWT signing.
/// <para>
/// Note: changing the secret (or this label) invalidates existing recovery-code hashes — as does any pepper
/// scheme. This template has no real users; a real deployment adopting this must have users regenerate codes.
/// </para>
/// </summary>
public sealed class RecoveryCodeHasher : IRecoveryCodeHasher
{
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("perezosoft:mfa:recovery-code-pepper:v1");
    private readonly byte[] _key;

    public RecoveryCodeHasher(IJwtSettings jwt) =>
        _key = HKDF.DeriveKey(HashAlgorithmName.SHA256,
            ikm: Encoding.UTF8.GetBytes(jwt.SecretKey), outputLength: 32, salt: null, info: Info);

    public string Hash(string canonicalCode) =>
        Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(canonicalCode ?? string.Empty)));

    public bool Verify(string canonicalCode, string storedHash)
    {
        var computed = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(canonicalCode ?? string.Empty));
        byte[] stored;
        try { stored = Convert.FromBase64String(storedHash ?? string.Empty); }
        catch (FormatException) { return false; } // malformed stored hash → reject, don't throw
        return CryptographicOperations.FixedTimeEquals(computed, stored);
    }
}
