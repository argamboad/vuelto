using System.Security.Cryptography;

namespace Vuelto.Api.Services;

/// <summary>
/// Generates cryptographically secure random tokens.
/// </summary>
public class TokenGenerator : ITokenGenerator
{
    public string GenerateToken()
    {
        var tokenBytes = RandomNumberGenerator.GetBytes(64);
        return Convert.ToBase64String(tokenBytes);
    }
}
