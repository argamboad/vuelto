using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Perezosoft.Shared.Ui.Auth;

namespace Perezosoft.Core.Tests.Auth;

/// <summary>
/// Entering impersonation swaps the in-memory access token in place, so the shared header (a
/// parameterless component) doesn't re-render on its own. AuthService raises <see cref="AuthService.IdentityChanged"/>
/// so the header can re-source the impersonated identity + staff flag — otherwise the badge/email/Admin
/// link keep showing the staff user for the whole session (QA-ADMIN-03). This proves the swap is
/// observable and that the derived identity follows the new token.
/// </summary>
public class ImpersonationIdentityTests
{
    [Fact]
    public void BeginImpersonation_RaisesIdentityChanged()
    {
        var auth = NewAuthService();
        var raised = 0;
        auth.IdentityChanged += () => raised++;

        auth.BeginImpersonation(Token(name: "Impersonated User", tenant: "Their Household", impersonatedBy: "staff-id"));

        Assert.Equal(1, raised);
    }

    [Fact]
    public void BeginImpersonation_SwapsDerivedIdentity_ToTheImpersonatedToken()
    {
        var auth = NewAuthService();
        auth.BeginImpersonation(Token(name: "Staff User", tenant: "Staff Household"));
        Assert.False(auth.IsImpersonating);

        auth.BeginImpersonation(Token(name: "Impersonated User", tenant: "Their Household", impersonatedBy: "staff-id"));

        Assert.True(auth.IsImpersonating);
        Assert.Equal("Impersonated User", auth.DisplayName);
        Assert.Equal("Their Household", auth.TenantName);
    }

    private static AuthService NewAuthService()
    {
        // No HTTP is exercised by BeginImpersonation; the handler stubs any incidental call.
        var http = new HttpClient(new StubHandler()) { BaseAddress = new Uri("https://api.test") };
        return new AuthService(http, NullLogger<AuthService>.Instance, new FakeSessionStore());
    }

    // Unsigned JWT (alg=none); AuthService only reads claims, it never validates the signature.
    private static string Token(string name, string tenant, string? impersonatedBy = null)
    {
        var claims = new List<Claim>
        {
            new("name", name),
            new(AppClaims.TenantName, tenant),
        };
        if (impersonatedBy is not null)
            claims.Add(new Claim(AppClaims.ImpersonatedBy, impersonatedBy));

        var jwt = new JwtSecurityToken(claims: claims, expires: DateTime.UtcNow.AddMinutes(30));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"is_staff":false}""", System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        public bool UsesBodyTransport => true;
        public Task<string?> GetRefreshTokenAsync() => Task.FromResult<string?>(null);
        public Task SaveRefreshTokenAsync(string refreshToken) => Task.CompletedTask;
        public Task ClearAsync() => Task.CompletedTask;
    }
}
