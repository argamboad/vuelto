using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Vuelto.Api.Configuration;

/// <summary>
/// The single source of truth for how app-issued JWT access tokens are validated. Both the
/// JWT-bearer handler (<c>Program.cs</c>) and <c>JwtTokenService.ValidateToken</c> build their
/// <see cref="TokenValidationParameters"/> from here, so the rules can't silently diverge
/// (CONF-7). Issuer == audience == <c>Jwt:Issuer</c>; zero clock skew.
/// </summary>
public static class JwtValidation
{
    public static TokenValidationParameters CreateParameters(this IJwtSettings settings) =>
        new()
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SecretKey)),
            ValidateIssuer = true,
            ValidIssuer = settings.Issuer,
            ValidateAudience = true,
            ValidAudience = settings.Issuer,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
}
