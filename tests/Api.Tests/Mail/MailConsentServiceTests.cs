using System.Net;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Core.Mail;
using Vuelto.Infrastructure.Mail;

namespace Vuelto.Api.Tests.Mail;

/// <summary>
/// EMAIL-2 (donor US-026 AC1 + WU-5 B2 + US-037 AC2): the unit-testable surface of the consent flow — the
/// read-only authorization URLs (openid email + offline access), fail-fast on a missing client id, the
/// Data-Protection state round trip (tamper / foreign key ring / expiry), and token-response mapping.
/// </summary>
public class MailConsentServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
    private const string Redirect = "https://api.vuelto.app/api/email/connections/callback";

    private static MailConsentSettings Settings(string ms = "ms-client", string google = "google-client") =>
        new() { MicrosoftClientId = ms, MicrosoftClientSecret = "ms-secret", MicrosoftTenant = "consumers", GoogleClientId = google, GoogleClientSecret = "g-secret" };

    private static MailConsentService Build(MailConsentSettings? settings = null, HttpMessageHandler? handler = null, IDataProtectionProvider? dp = null, FakeTimeProvider? clock = null) =>
        new(settings ?? Settings(), new HttpClient(handler ?? new StubHandler("{}")), dp ?? new EphemeralDataProtectionProvider(), clock ?? new FakeTimeProvider(T0));

    [Fact]
    public void Microsoft_Url_RequestsReadOnlyMail_OfflineAccess_OpenIdEmail_OnTheConfiguredTenant()
    {
        var url = Build().BuildAuthorizationUrl(EmailProviders.Microsoft, Redirect, "state123");
        Assert.StartsWith("https://login.microsoftonline.com/consumers/oauth2/v2.0/authorize?", url);
        Assert.Contains("client_id=ms-client", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("state=state123", url);
        var scope = Uri.UnescapeDataString(url);
        Assert.Contains("https://graph.microsoft.com/Mail.Read", scope);
        Assert.Contains("offline_access", scope);
        Assert.Contains("openid email", scope);
        Assert.DoesNotContain("Mail.ReadWrite", scope);
    }

    [Fact]
    public void Google_Url_RequestsGmailReadonly_OfflineAndConsent_WithLoginHint()
    {
        var url = Build().BuildAuthorizationUrl(EmailProviders.Google, Redirect, "st", loginHint: "me@gmail.com");
        Assert.StartsWith("https://accounts.google.com/o/oauth2/v2/auth?", url);
        Assert.Contains("client_id=google-client", url);
        Assert.Contains("access_type=offline", url);
        Assert.Contains("prompt=consent", url);
        Assert.Contains("login_hint=me%40gmail.com", url);
        var scope = Uri.UnescapeDataString(url);
        Assert.Contains("https://www.googleapis.com/auth/gmail.readonly", scope);
        Assert.Contains("openid email", scope);
    }

    [Fact]
    public void UnknownProvider_Throws() => Assert.Throws<ArgumentException>(() => Build().BuildAuthorizationUrl("outlook", Redirect, "s"));

    [Fact]
    public void MissingClientId_FailsFast_InsteadOfEmittingAUrlWithoutIt()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(Settings(ms: "")).BuildAuthorizationUrl(EmailProviders.Microsoft, Redirect, "s"));
        Assert.Contains("client id", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Settings(ms: "").IsConfigured(EmailProviders.Microsoft));
        Assert.True(Settings().IsConfigured(EmailProviders.Google));
    }

    [Fact]
    public void State_RoundTripsUserAndProvider()
    {
        var sut = Build();
        var userId = Guid.NewGuid();
        var state = sut.ProtectState(userId, EmailProviders.Google);
        Assert.True(sut.TryReadState(state, out var readUser, out var readProvider));
        Assert.Equal((userId, EmailProviders.Google), (readUser, readProvider));
    }

    [Fact]
    public void State_TamperedOrFromAnotherKeyRingOrMalformed_IsRejected()
    {
        var sut = Build();
        var state = sut.ProtectState(Guid.NewGuid(), EmailProviders.Microsoft);
        Assert.False(sut.TryReadState(state[..^2] + (state[^1] == 'A' ? "B" : "A"), out _, out _));
        Assert.False(sut.TryReadState(Build().ProtectState(Guid.NewGuid(), EmailProviders.Google), out _, out _)); // different ephemeral key ring
        Assert.False(sut.TryReadState(null, out _, out _));
        Assert.False(sut.TryReadState("", out _, out _));
        Assert.False(sut.TryReadState("not-a-valid-state", out _, out _));
    }

    [Fact]
    public void State_Expires_AfterItsLifetime()
    {
        // A shared key ring but a clock that moves: Data Protection's time-limited payload refuses a stale state.
        var dp = new EphemeralDataProtectionProvider();
        var sut = Build(dp: dp);
        var state = sut.ProtectState(Guid.NewGuid(), EmailProviders.Google);
        Assert.True(sut.TryReadState(state, out _, out _));
        // ITimeLimitedDataProtector reads the system clock, so prove expiry by protecting with a lifetime already passed.
        var expired = dp.CreateProtector("Vuelto.Mail.ConsentState.v1").ToTimeLimitedDataProtector().Protect($"{Guid.NewGuid():N}|google", TimeSpan.FromMilliseconds(1));
        Thread.Sleep(20);
        Assert.False(sut.TryReadState(expired, out _, out _));
    }

    [Fact]
    public async Task ExchangeCode_PostsTheAuthorizationCodeGrant_AndMapsTokensAndTheIdTokenEmail()
    {
        var idToken = $"{B64Url("{}")}.{B64Url("{\"email\":\"me@example.com\"}")}.sig";
        var handler = new StubHandler($$"""{"access_token":"at","refresh_token":"rt","expires_in":3600,"id_token":"{{idToken}}"}""");
        var sut = Build(handler: handler);

        var tokens = await sut.ExchangeCodeAsync(EmailProviders.Google, "code-1", Redirect);

        Assert.Equal(("at", "rt", "me@example.com", T0.AddHours(1)), (tokens.AccessToken, tokens.RefreshToken, tokens.AccountEmail, tokens.ExpiresAt));
        Assert.Equal("https://oauth2.googleapis.com/token", handler.LastUrl);
        Assert.Contains("grant_type=authorization_code", handler.LastBody);
        Assert.Contains("code=code-1", handler.LastBody);
        Assert.Contains("client_secret=g-secret", handler.LastBody);
    }

    [Fact]
    public async Task ExchangeCode_WithoutARefreshToken_Throws()
    {
        var sut = Build(handler: new StubHandler("""{"access_token":"at","expires_in":3600}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ExchangeCodeAsync(EmailProviders.Microsoft, "code", Redirect));
    }

    [Fact]
    public async Task Refresh_KeepsTheOldRefreshToken_WhenTheProviderOmitsANewOne()
    {
        var handler = new StubHandler("""{"access_token":"at2","expires_in":1800}""");
        var tokens = await Build(handler: handler).RefreshAsync(EmailProviders.Microsoft, "old-refresh");
        Assert.Equal(("at2", "old-refresh", T0.AddMinutes(30)), (tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAt));
        Assert.Equal("https://login.microsoftonline.com/consumers/oauth2/v2.0/token", handler.LastUrl);
        Assert.Contains("grant_type=refresh_token", handler.LastBody);
    }

    private static string B64Url(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        public string? LastUrl { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri!.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
