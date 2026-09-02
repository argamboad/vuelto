using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Vuelto.Shared.Ui.Auth;

namespace Vuelto.Ui.Tests.Infrastructure;

/// <summary>
/// Builds an access token the way <see cref="AuthService"/> reads it: it parses claims with
/// <see cref="JwtSecurityTokenHandler.ReadJwtToken"/> and never validates the signature, so an unsigned
/// token with the right claims + a future <c>exp</c> is enough to drive real signed-in state.
/// </summary>
public static class TestJwt
{
    public static string Build(
        string userId = "11111111-1111-1111-1111-111111111111",
        string? name = "Ada Lovelace",
        string? tenantName = "Test Household",
        string? tenantId = "22222222-2222-2222-2222-222222222222",
        string? locale = null,
        string? theme = null,
        string? impersonatedBy = null,
        TimeSpan? lifetime = null)
    {
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, userId) };
        void Add(string type, string? value) { if (value is not null) claims.Add(new Claim(type, value)); }
        Add("name", name);
        Add(AppClaims.TenantName, tenantName);
        Add(AppClaims.TenantId, tenantId);
        Add(AppClaims.Locale, locale);
        Add(AppClaims.Theme, theme);
        Add(AppClaims.ImpersonatedBy, impersonatedBy);

        var now = DateTime.UtcNow;
        var expires = now.Add(lifetime ?? TimeSpan.FromHours(1));
        var token = new JwtSecurityToken(
            claims: claims,
            // A negative lifetime builds an ALREADY-EXPIRED token (notBefore must precede expires).
            notBefore: expires < now ? expires.AddMinutes(-10) : now,
            expires: expires);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
