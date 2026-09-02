namespace Perezosoft.Api.Services;

/// <summary>
/// Hashes tokens for secure storage.
/// Separated from generation to allow algorithm substitution.
/// </summary>
public interface ITokenHasher
{
    string HashToken(string token);

    /// <summary>
    /// Timing-safe check that <paramref name="rawToken"/> hashes to
    /// <paramref name="storedHash"/>. Use this instead of <c>==</c> when comparing a
    /// presented secret to a stored hash (notably the low-entropy OTP code).
    /// </summary>
    bool Verify(string rawToken, string storedHash);
}
