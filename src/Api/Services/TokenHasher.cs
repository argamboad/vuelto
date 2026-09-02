using System.Security.Cryptography;
using System.Text;

namespace Perezosoft.Api.Services;

/// <summary>
/// Hashes tokens using SHA256.
/// </summary>
public class TokenHasher : ITokenHasher
{
    public string HashToken(string token)
    {
        var tokenBytes = Encoding.UTF8.GetBytes(token);
        var hashBytes = SHA256.HashData(tokenBytes);
        return Convert.ToBase64String(hashBytes);
    }

    public bool Verify(string rawToken, string storedHash)
    {
        var computed = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken ?? string.Empty));

        byte[] stored;
        try { stored = Convert.FromBase64String(storedHash ?? string.Empty); }
        catch (FormatException) { return false; }

        // FixedTimeEquals is constant-time and returns false (not throws) on a length
        // mismatch, so a malformed stored hash is simply rejected.
        return CryptographicOperations.FixedTimeEquals(computed, stored);
    }
}
