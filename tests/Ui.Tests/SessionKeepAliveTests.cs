using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Vuelto.Shared.Ui;
using Vuelto.Shared.Ui.Auth;
using Vuelto.Shared.Ui.Layout;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// "I have to sign in every day" (found downstream, 2026-09-22). The refresh token lives 30 days, but the client
/// only spent it once, at startup: an app left open (a tab overnight, the phone app in the background) kept a
/// dead 60-minute access token and nothing renewed it. And any failed refresh — a timeout, a 502 while the host
/// cold-starts, no signal — was read as "signed out": the native app deleted its stored refresh token, so a
/// sleeping server cost the user their session for good. These pin the fix: the session renews itself ahead of
/// expiry (timer, before a request, on resume), and only the server saying no ends it.
/// </summary>
public class SessionKeepAliveTests : ComponentTestBase
{
    private const string RefreshPath = "/api/auth/refresh";

    private int Refreshes => Http.Requests.Count(r => r.RequestUri!.AbsolutePath == RefreshPath);

    private void StubRefresh(string name, TimeSpan? lifetime = null) =>
        Http.On(HttpMethod.Post, RefreshPath, $"{{\"access_token\":\"{TestJwt.Build(name: name, lifetime: lifetime)}\"}}");

    private AuthService NativeAuth(FakeSessionStore store) =>
        new(new HttpClient(Http) { BaseAddress = new Uri("http://localhost") },
            NullLogger<AuthService>.Instance, store, timeProvider: Time);

    private static async Task<FakeSessionStore> StoreHolding(string refreshToken)
    {
        var store = new FakeSessionStore(usesBodyTransport: true);
        await store.SaveRefreshTokenAsync(refreshToken);
        return store;
    }

    // ── renew ahead of expiry ────────────────────────────────────────────────

    [Fact]
    public async Task OpenSession_RenewsItselfBeforeTheAccessTokenExpires()
    {
        await SignInAsync(name: "First");
        StubRefresh("Renewed", lifetime: TimeSpan.FromHours(2)); // minted now, so it must outlive the jump below

        Time.Advance(TimeSpan.FromMinutes(58));             // an hour-long token, nothing due yet
        Assert.Equal(1, Refreshes);

        Time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1)); // a minute before expiry
        await WaitUntil(() => Auth.DisplayName == "Renewed");
        Assert.Equal(2, Refreshes);

        Time.Advance(TimeSpan.FromMinutes(2));               // past the FIRST token's expiry
        Assert.True(Auth.IsAuthenticated);
    }

    [Fact]
    public async Task ARequest_InsideTheRenewalWindow_RenewsBeforeSending()
    {
        await SignInAsync(name: "First");

        // The timed renewal ran while the network was down; its retry hasn't come round yet. A request made
        // now must not go out on a token about to die (it would 401 and the page would look signed out).
        Http.OnUnreachable(HttpMethod.Post, RefreshPath);
        Time.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(1));
        await WaitUntil(() => Refreshes == 2);
        Time.Advance(TimeSpan.FromSeconds(10));
        StubRefresh("Renewed");

        var token = await Auth.GetFreshAccessTokenAsync();

        Assert.Equal(3, Refreshes);
        Assert.Equal("Renewed", Auth.DisplayName);
        Assert.Equal(Auth.AccessToken, token);
    }

    [Fact]
    public async Task ARequest_WithALiveToken_DoesNotRefresh()
    {
        await SignInAsync();

        var token = await Auth.GetFreshAccessTokenAsync();

        Assert.NotNull(token);
        Assert.Equal(1, Refreshes); // the sign-in only
    }

    [Fact]
    public async Task ARequest_NeverSignedIn_DoesNotTryToRefresh()
    {
        // Anonymous pages (login, the provider probe) call the API too — they must not spend a refresh each.
        Assert.Null(await Auth.GetFreshAccessTokenAsync());
        Assert.Equal(0, Refreshes);
    }

    [Fact]
    public async Task AfterLogout_NothingRenews()
    {
        await SignInAsync();
        Http.On(HttpMethod.Post, "/api/auth/logout");
        await Auth.LogoutAsync();

        Time.Advance(TimeSpan.FromHours(2));

        Assert.Null(await Auth.GetFreshAccessTokenAsync());
        Assert.Equal(1, Refreshes);
    }

    [Fact]
    public async Task WhileImpersonating_TheRenewalNeverSwapsTheIdentityBack()
    {
        // The staff session's renewal timer must not fire under an impersonation: a refresh restores the
        // STAFF identity, which would end the "sign in as" silently mid-task.
        await SignInAsync(name: "Staff Member");
        Auth.BeginImpersonation(TestJwt.Build(name: "Target User", impersonatedBy: Guid.NewGuid().ToString(),
            lifetime: TimeSpan.FromMinutes(90)));

        Time.Advance(TimeSpan.FromMinutes(75));
        await Auth.GetFreshAccessTokenAsync();

        Assert.Equal(1, Refreshes);
        Assert.Equal("Target User", Auth.DisplayName);
    }

    // ── only the server saying no ends a session ────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Native_ServerError_KeepsTheStoredRefreshToken(HttpStatusCode status)
    {
        var store = await StoreHolding("rt-1");
        var auth = NativeAuth(store);
        Http.On(HttpMethod.Post, RefreshPath, "<html>waking up</html>", status);

        Assert.False(await auth.TryRefreshAsync());

        Assert.Equal("rt-1", await store.GetRefreshTokenAsync()); // the next attempt can still use it
    }

    [Fact]
    public async Task Native_Unreachable_KeepsTheStoredRefreshToken()
    {
        var store = await StoreHolding("rt-1");
        var auth = NativeAuth(store);
        Http.OnUnreachable(HttpMethod.Post, RefreshPath);

        Assert.False(await auth.TryRefreshAsync());

        Assert.Equal("rt-1", await store.GetRefreshTokenAsync());
    }

    [Fact]
    public async Task Native_Rejected_ClearsTheStoredRefreshToken()
    {
        var store = await StoreHolding("rt-1");
        var auth = NativeAuth(store);
        Http.On(HttpMethod.Post, RefreshPath, """{"error":"invalid_refresh_token"}""", HttpStatusCode.Unauthorized);

        Assert.False(await auth.TryRefreshAsync());

        Assert.Null(await store.GetRefreshTokenAsync()); // revoked or expired: it is dead, drop it
    }

    [Fact]
    public async Task MidSession_ServerError_KeepsTheSession_AndTriesAgainShortly()
    {
        await SignInAsync(name: "First");
        var signedOut = 0;
        Auth.SignedOut += () => signedOut++;
        Http.On(HttpMethod.Post, RefreshPath, "", HttpStatusCode.BadGateway);

        Time.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(1)); // the renewal runs — and fails
        await WaitUntil(() => Refreshes == 2);

        Assert.Equal(0, signedOut);
        Assert.True(Auth.IsAuthenticated); // the current token still has a minute to live

        StubRefresh("Renewed");
        await AdvanceUntil(() => Refreshes == 3, limit: AuthService.RenewRetryDelay + TimeSpan.FromSeconds(5));
        await WaitUntil(() => Auth.DisplayName == "Renewed");
    }

    [Fact]
    public async Task Startup_RetriesWhileTheServerWakesUp_ThenSignsIn()
    {
        var store = await StoreHolding("rt-1");
        var auth = NativeAuth(store);
        Http.On(HttpMethod.Post, RefreshPath, "", HttpStatusCode.BadGateway);

        var init = auth.InitializeAsync();
        await WaitUntil(() => Refreshes == 1);
        Assert.False(init.IsCompleted); // still on the loading spinner, not bounced to the login page

        Http.On(HttpMethod.Post, RefreshPath,
            $"{{\"access_token\":\"{TestJwt.Build()}\",\"refresh_token\":\"rt-2\"}}");
        await AdvanceUntil(() => init.IsCompleted, limit: AuthService.StartupRetryDelays[0]);
        await init;

        Assert.True(auth.IsAuthenticated);
        Assert.Equal("rt-2", await store.GetRefreshTokenAsync());
    }

    [Fact]
    public async Task Startup_ServerStillDown_ShowsSignedOut_ButKeepsTheToken()
    {
        var store = await StoreHolding("rt-1");
        var auth = NativeAuth(store);
        Http.OnUnreachable(HttpMethod.Post, RefreshPath);

        var init = auth.InitializeAsync();
        var totalBackoff = AuthService.StartupRetryDelays.Aggregate(TimeSpan.Zero, (sum, d) => sum + d);
        await AdvanceUntil(() => init.IsCompleted, limit: totalBackoff);
        await init;

        Assert.Equal(1 + AuthService.StartupRetryDelays.Count, Refreshes);
        Assert.False(auth.IsAuthenticated);
        Assert.Equal("rt-1", await store.GetRefreshTokenAsync()); // the next launch tries again
    }

    [Fact]
    public async Task Startup_Rejected_DoesNotRetry()
    {
        var store = await StoreHolding("rt-1");
        var auth = NativeAuth(store);
        Http.On(HttpMethod.Post, RefreshPath, """{"error":"invalid_refresh_token"}""", HttpStatusCode.Unauthorized);

        await auth.InitializeAsync();

        Assert.Equal(1, Refreshes);
        Assert.False(auth.IsAuthenticated);
    }

    // ── coming back to the app ───────────────────────────────────────────────

    [Fact]
    public async Task Resume_RenewsASessionWhoseRenewalFailedWhileAway()
    {
        await SignInAsync(name: "First");
        Render<MainLayout>(ps => ps.Add(m => m.Body, b => b.AddMarkupContent(0, "<div>body</div>")));

        // The renewal ran while the phone was in a pocket with no signal, and its retry hasn't come round.
        Http.OnUnreachable(HttpMethod.Post, RefreshPath);
        Time.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(1));
        await WaitUntil(() => Refreshes == 2);
        Time.Advance(TimeSpan.FromSeconds(20));

        StubRefresh("Renewed");
        Services.GetRequiredService<AppResumeNotifier>().Notify();

        await WaitUntil(() => Auth.DisplayName == "Renewed");
        Assert.Equal(3, Refreshes);
    }

    /// <summary>
    /// Moves the fake clock forward a second at a time, letting the async work it wakes run in between, until
    /// <paramref name="condition"/> holds — never further than <paramref name="limit"/>. Stepping (rather than
    /// one big jump) is what makes it safe to call before the code under test has registered its next timer.
    /// </summary>
    private async Task AdvanceUntil(Func<bool> condition, TimeSpan limit)
    {
        var moved = TimeSpan.Zero;
        while (!condition() && moved < limit)
        {
            await Task.Delay(10);
            Time.Advance(TimeSpan.FromSeconds(1));
            moved += TimeSpan.FromSeconds(1);
        }
        await WaitUntil(condition);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }
}
