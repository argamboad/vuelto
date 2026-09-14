using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;

namespace Vuelto.Api.Tests.Auth;

/// <summary>
/// GATES-2 (ADR-027): a refusal has to arrive as something a person can act on, on whichever path they
/// happened to use. A 500 would be the default outcome of a gate that throws from deep inside account
/// creation, and "something went wrong" is exactly the wrong message for "you are not invited yet".
/// <para>
/// Driven at the wire against the real app so the mapping is proven where it actually has to hold — the
/// decision itself is unit-tested in <see cref="SignupGateTests"/>.
/// </para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class SignupRefusalSurfacingTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    /// <summary>
    /// A host whose green list admits exactly one address, which is nobody in these tests unless named.
    /// Returned as the factory rather than just a client so a test can mint credentials through the SAME
    /// host it then calls: issuing from one host and redeeming on another shares a database but not a
    /// request pipeline, and the seam is not worth the ambiguity when a redemption comes back invalid.
    /// </summary>
    private WebApplicationFactory<Program> RestrictedHost() =>
        _factory.WithWebHostBuilder(b => b.UseSetting("Signup:AllowedEmails:0", "only-this-one@example.com"));

    private static HttpClient ClientOf(WebApplicationFactory<Program> host) =>
        host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<string> IssueOtpAsync(WebApplicationFactory<Program> host, string email)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPasswordlessService>().IssueOtpAsync(email);
    }

    private static async Task<string> IssueMagicLinkAsync(WebApplicationFactory<Program> host, string email)
    {
        using var scope = host.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPasswordlessService>().IssueMagicLinkTokenAsync(email);
    }

    [Fact]
    public async Task Otp_ForAnUninvitedAddress_Is403_WithAnActionableCode()
    {
        var host = RestrictedHost();
        const string email = "stranger-otp@example.com";

        // A perfectly VALID code, minted the same way the send endpoint mints one. The point is that the
        // gate fires at redemption, because that is where the account would be created.
        var code = await IssueOtpAsync(host, email);
        var client = ClientOf(host);

        var res = await client.PostAsJsonAsync("/api/auth/otp/verify", new { email, code });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode); // not 401 (wrong code) and not 500
        var body = await res.Content.ReadFromJsonAsync<ErrorBody>();
        Assert.Equal("signup_not_allowed", body!.Error);
    }

    [Fact]
    public async Task Otp_ForAnAddressOnTheList_StillSignsIn()
    {
        var host = RestrictedHost();
        const string email = "only-this-one@example.com";

        var code = await IssueOtpAsync(host, email);
        var client = ClientOf(host);

        var res = await client.PostAsJsonAsync("/api/auth/otp/verify", new { email, code });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task MagicLink_ForAnUninvitedAddress_RedirectsToTheLoginPage_NotAnErrorPage()
    {
        var host = RestrictedHost();
        const string email = "stranger-link@example.com";

        var token = await IssueMagicLinkAsync(host, email);
        var client = ClientOf(host);

        // Escape the token like the real email link does: a raw token can carry characters that a query
        // string re-reads as something else, which hashes to a miss and reads as "invalid link".
        var res = await client.GetAsync(
            $"/api/auth/magic-link/verify?token={Uri.EscapeDataString(token)}&email={Uri.EscapeDataString(email)}");

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Contains("error=signup_not_allowed", res.Headers.Location!.ToString());
    }

    private sealed record ErrorBody(string Error);
}
