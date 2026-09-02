using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Api.Tests.Integration;

/// <summary>
/// v2 audit B8-2 (MFA-2/3/4, ADR-012): proves at the HTTP boundary that MFA step-up is enforced on
/// EVERY sign-in path — OTP, native OAuth code exchange, magic-link, and web OAuth callback. When the
/// resolved user has MFA enabled, the primary factor must yield a **challenge**, never a session; only a
/// valid TOTP posted to /api/auth/mfa/verify completes sign-in. This is the wire-level guarantee behind
/// the unit-tested <see cref="IMfaLoginService"/> seam — it catches an entry point that forgets to route
/// through step-up (the exact bypass the audit worried about).
/// </summary>
[Collection(IntegrationCollection.Name)]
public class MfaStepUpIntegrationTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    // ── (a) Email OTP ─────────────────────────────────────────────────────────
    [Fact]
    public async Task Otp_WithMfaEnabled_ChallengesThenCompletesWithTotp()
    {
        var (user, secret) = await _factory.SeedMfaUserAsync();
        var otpCode = await IssueAsync(pwl => pwl.IssueOtpAsync(user.Email));
        var client = _factory.CreateClient();

        // Primary factor: OTP is correct, but MFA is on ⇒ a challenge, NOT a session.
        var otpResp = await client.PostAsJsonAsync("/api/auth/otp/verify", new { email = user.Email, code = otpCode });
        var challenge = await AssertChallengeAsync(otpResp);

        // Second factor: a valid TOTP posted to mfa/verify completes sign-in.
        var mfaResp = await client.PostAsJsonAsync("/api/auth/mfa/verify",
            new { challenge, code = IntegrationTestFactory.Totp(secret) });
        await AssertSessionAsync(mfaResp);
    }

    [Fact]
    public async Task MfaVerify_WithWrongCode_Returns401_AndIssuesNoSession()
    {
        var (user, _) = await _factory.SeedMfaUserAsync();
        var otpCode = await IssueAsync(pwl => pwl.IssueOtpAsync(user.Email));
        var client = _factory.CreateClient();

        var challenge = await AssertChallengeAsync(
            await client.PostAsJsonAsync("/api/auth/otp/verify", new { email = user.Email, code = otpCode }));

        var mfaResp = await client.PostAsJsonAsync("/api/auth/mfa/verify", new { challenge, code = "000000" });
        Assert.Equal(HttpStatusCode.Unauthorized, mfaResp.StatusCode);
    }

    [Fact]
    public async Task Otp_WithoutMfa_IssuesSessionDirectly()
    {
        // Contrast: no MFA ⇒ the OTP path returns a session immediately (so the challenge above is
        // genuinely the step-up, not a quirk of the endpoint).
        var user = await _factory.SeedUserAsync(TenantRoles.Owner);
        var otpCode = await IssueAsync(pwl => pwl.IssueOtpAsync(user.Email));

        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/auth/otp/verify", new { email = user.Email, code = otpCode });

        await AssertSessionAsync(resp);
    }

    // ── (b) Native OAuth code exchange ─────────────────────────────────────────
    [Fact]
    public async Task NativeExchange_WithMfaEnabled_ChallengesThenCompletesWithTotp()
    {
        var (user, secret) = await _factory.SeedMfaUserAsync();
        var code = Resolve<INativeAuthCodeService>(s => s.Issue(user.UserId, "google"));
        var client = _factory.CreateClient();

        var challenge = await AssertChallengeAsync(
            await client.PostAsJsonAsync("/api/auth/native/exchange", new { code }));

        var mfaResp = await client.PostAsJsonAsync("/api/auth/mfa/verify",
            new { challenge, code = IntegrationTestFactory.Totp(secret) });
        await AssertSessionAsync(mfaResp);
    }

    // ── (c) Magic link ─────────────────────────────────────────────────────────
    [Fact]
    public async Task MagicLink_WithMfaEnabled_RedirectsToStepUp_NotASession()
    {
        var (user, _) = await _factory.SeedMfaUserAsync();
        var token = await IssueAsync(pwl => pwl.IssueMagicLinkTokenAsync(user.Email));
        var client = _factory.CreateNoRedirectClient();

        var resp = await client.GetAsync(
            $"/api/auth/magic-link/verify?token={Uri.EscapeDataString(token)}&email={Uri.EscapeDataString(user.Email)}");

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("mfa=", resp.Headers.Location!.ToString());          // bounced to step-up…
        Assert.DoesNotContain("auth-callback", resp.Headers.Location!.ToString()); // …not signed in
    }

    // ── (d) Web OAuth callback ─────────────────────────────────────────────────
    [Fact]
    public async Task OAuthCallback_WithMfaEnabled_RedirectsToStepUp_NotASession()
    {
        var (user, _) = await _factory.SeedMfaUserAsync();
        const string providerUserId = "google-oauth-subject-123";
        await LinkProviderAsync(user.UserId, "google", providerUserId); // callback resolves this login → our MFA user

        var client = _factory.CreateNoRedirectClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/callback/google");
        request.Headers.Add("X-Test-Provider-User", providerUserId); // consumed by TestExternalAuthHandler
        request.Headers.Add("X-Test-Email", user.Email);

        var resp = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("mfa=", resp.Headers.Location!.ToString());
        Assert.DoesNotContain("auth-callback", resp.Headers.Location!.ToString());
    }

    // ── helpers ────────────────────────────────────────────────────────────────
    private async Task<T> IssueAsync<T>(Func<IPasswordlessService, Task<T>> issue)
    {
        using var scope = _factory.Services.CreateScope();
        return await issue(scope.ServiceProvider.GetRequiredService<IPasswordlessService>());
    }

    private string Resolve<T>(Func<T, string> use) where T : notnull
    {
        using var scope = _factory.Services.CreateScope();
        return use(scope.ServiceProvider.GetRequiredService<T>());
    }

    private async Task LinkProviderAsync(Guid userId, string provider, string providerUserId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Set<UserLogin>().Add(new UserLogin { UserId = userId, Provider = provider, ProviderUserId = providerUserId });
        await db.SaveChangesAsync();
    }

    /// <summary>Asserts a 200 MFA-required response and returns the challenge.</summary>
    private static async Task<string> AssertChallengeAsync(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("challenge", out var challenge), "expected an MFA challenge");
        Assert.False(doc.RootElement.TryGetProperty("access_token", out _), "must not issue a session before step-up");
        return challenge.GetString()!;
    }

    /// <summary>Asserts a 200 token response carrying an access token.</summary>
    private static async Task AssertSessionAsync(HttpResponseMessage resp)
    {
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("access_token", out var token) && !string.IsNullOrEmpty(token.GetString()),
            "expected an access token after successful sign-in");
    }
}
