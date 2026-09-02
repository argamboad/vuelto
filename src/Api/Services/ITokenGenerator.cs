namespace Vuelto.Api.Services;

/// <summary>
/// Generates cryptographically secure tokens.
/// Separated from validation/persistence to allow algorithm substitution.
/// </summary>
public interface ITokenGenerator
{
    string GenerateToken();
}
