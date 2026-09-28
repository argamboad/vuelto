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

    /// <summary>A refresh answer shaped like the API's: a token with no nbf, plus expires_in — the server's word on the lifetime.</summary>
    private void StubServerShapedRefresh(string name, TimeSpan lifetime, TimeSpan? serverClockOffset = null) =>
        Http.On(HttpMethod.Post, RefreshPath,
            $"{{\"access_token\":\"{TestJwt.Build(name: name, lifetime: lifetime, withNotBefore: false, serverClockOffset: serverClockOffset)}\",\"expires_in\":{(int)lifetime.TotalSeconds}}}");

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

    // ── the shell follows the session, not the token; a rejection ends it once (v4 T33, R84/R111) ──

    [Fact]
    public async Task UnreachableThroughExpiry_ThenRejected_RaisesSignedOutExactlyOnce()
    {
        // AUTH-5: during a server outage that outlasts the access token the layout dropped to the anonymous
        // shell as if signed out, and if the server then rejected the token, SignedOut was skipped (the token
        // was already expired when the session cleared) — so the device-preference wipe never ran.
        await SignInAsync(name: "First", theme: "dark");
        var nav = (Bunit.TestDoubles.BunitNavigationManager)Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("/household");
        var cut = Render<MainLayout>(ps => ps.Add(m => m.Body, b => b.AddMarkupContent(0, "<div id='page-body'>household</div>")));
        var signedOut = 0;
        Auth.SignedOut += () => signedOut++;
        Http.OnUnreachable(HttpMethod.Post, RefreshPath);

        Time.Advance(TimeSpan.FromMinutes(62)); // every renewal failed; the token is past its expiry
        await Task.Delay(50);
        cut.Render();

        Assert.Equal(0, signedOut);
        Assert.True(Auth.HasSession);
        Assert.NotEmpty(cut.FindAll("main.main-content")); // the shell stays: nothing has said the session is over

        Http.On(HttpMethod.Post, RefreshPath, """{"error":"invalid_refresh_token"}""", HttpStatusCode.Unauthorized);
        await AdvanceUntil(() => signedOut == 1, limit: AuthService.RenewRetryCap + TimeSpan.FromSeconds(5));

        Assert.False(Auth.HasSession);
        Assert.True(ThemeStore.Cleared);
        Assert.EndsWith("/login", nav.Uri);
        Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(50);
        Assert.Equal(1, signedOut);
    }

    [Theory]
    [InlineData("/household", true)]
    [InlineData("/join", false)] // an anonymous page: the user stays where they are
    public async Task MidSessionRejected_NavigatesToLogin_FromAProtectedRoute(string path, bool bounces)
    {
        // UX-11: after a mid-session rejection (logout in another tab, revoke-all, a staff MFA reset) the user
        // sat on a protected page with no chrome while every call returned 401 until they reloaded.
        await SignInAsync(name: "First");
        var nav = (Bunit.TestDoubles.BunitNavigationManager)Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo(path);
        Render<MainLayout>(ps => ps.Add(m => m.Body, b => b.AddMarkupContent(0, "<div>page</div>")));
        Http.On(HttpMethod.Post, RefreshPath, """{"error":"invalid_refresh_token"}""", HttpStatusCode.Unauthorized);

        Time.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(1)); // the renewal runs — and is refused
        await WaitUntil(() => !Auth.HasSession);
        await Task.Delay(50);

        Assert.Equal(bounces, nav.Uri.EndsWith("/login", StringComparison.Ordinal));
    }

    // ── the token's lifetime is the server's, not the device clock's (v4 T32, R126) ──

    [Fact]
    public async Task DeviceClockSlow_ARequestAfterRealExpiry_RenewsBeforeSending()
    {
        // LB-UI-14(a): a phone 3 minutes slow read the JWT's exp against its own clock and believed a dead token
        // had 3 minutes left — every call in that window 401'd. The lifetime must be counted from receipt, on
        // the device clock, using the server's expires_in. (Device 3 min slow ≡ server 3 min ahead.)
        StubServerShapedRefresh("First", TimeSpan.FromHours(1), serverClockOffset: TimeSpan.FromMinutes(3));
        await Auth.InitializeAsync();
        var first = Auth.AccessToken;
        StubServerShapedRefresh("Renewed", TimeSpan.FromHours(1));

        Time.Advance(TimeSpan.FromMinutes(60) + TimeSpan.FromSeconds(10)); // past the token's REAL expiry

        var token = await Auth.GetFreshAccessTokenAsync();
        Assert.NotNull(token);
        Assert.NotEqual(first, token);
        Assert.Equal("Renewed", Auth.DisplayName);
    }

    [Fact]
    public async Task DeviceClockFast_NeverLoopsRotations_AndReportsSignedIn()
    {
        // LB-UI-14(b): a phone an hour fast saw every fresh token as already expired — a signed-out screen while
        // the client rotated the refresh token every 30 s, ~2,880 times a day. (Device 61 min fast ≡ server 61 min behind.)
        StubServerShapedRefresh("First", TimeSpan.FromHours(1), serverClockOffset: TimeSpan.FromMinutes(-61));

        await Auth.InitializeAsync();

        Assert.True(Auth.IsAuthenticated);
        Assert.Equal("First", Auth.DisplayName);
        Time.Advance(TimeSpan.FromMinutes(5)); // the old 30 s loop would have rotated ~10 times by now
        await Task.Delay(50);
        Assert.Equal(1, Refreshes);
    }

    [Fact]
    public async Task RenewalLead_ForAServerShapedToken_IsAQuarterOfTheLifetime()
    {
        // LB-UI-15: the quarter-of-lifetime cap on the renewal lead was computed from nbf, which the API's tokens
        // never carry — so a deployment with 2-minute tokens renewed a minute early and fell into the 30 s loop.
        // From expires_in: a 2-minute token renews at 90 s, not 60.
        StubServerShapedRefresh("First", TimeSpan.FromMinutes(2));
        await Auth.InitializeAsync();
        StubServerShapedRefresh("Renewed", TimeSpan.FromMinutes(2));

        Time.Advance(TimeSpan.FromSeconds(89));
        await Task.Delay(50);
        Assert.Equal(1, Refreshes);

        Time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntil(() => Refreshes == 2);
    }

    [Fact]
    public async Task ABearerRequest_Refused401_RefreshesAndRetriesOnce()
    {
        // LB-UI-14 (the handler half): whatever the clocks say, a 401 on a request sent with a held session is
        // the server's word that the token is no good now — refresh once and resend; a second 401 stands.
        await SignInAsync(name: "First");
        StubRefresh("Renewed");
        Http.OnSequence(HttpMethod.Get, "/api/household", (HttpStatusCode.Unauthorized, "{}"), (HttpStatusCode.OK, """{"name":"Casa"}"""));
        var client = new HttpClient(new BearerRetryHandler(Auth) { InnerHandler = Http }) { BaseAddress = new Uri("http://localhost") };

        var response = await client.GetAsync("/api/household");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, Refreshes);
        var sends = Http.Requests.Where(r => r.RequestUri!.AbsolutePath == "/api/household").ToList();
        Assert.Equal(2, sends.Count);
        Assert.Equal("Renewed", Auth.DisplayName);
        Assert.Equal(Auth.AccessToken, sends[1].Headers.Authorization!.Parameter); // resent with the NEW token

        // Still refused after the refresh: the 401 stands, and there is no third attempt.
        Http.On(HttpMethod.Get, "/api/household", "{}", HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/household")).StatusCode);
        Assert.Equal(4, Http.Requests.Count(r => r.RequestUri!.AbsolutePath == "/api/household"));
    }

    private sealed class BearerRetryHandler(AuthService auth) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            BearerRetry.SendAsync(auth, request, base.SendAsync, cancellationToken);
    }

    // ── a refresh in flight across a change of session is discarded (v4 T31, R125) ──

    [Fact]
    public async Task ARefreshInFlight_WhenImpersonationBegins_IsDiscarded()
    {
        // LB-UI-11: the timer's refresh is on the wire when the admin clicks "Sign in as". Its answer — a staff
        // token — must not overwrite the impersonation token: that ended the impersonation the moment it began
        // while the header still showed the target.
        await SignInAsync(name: "Staff Member");
        var signedIn = 0;
        Auth.SignedIn += () => signedIn++;
        // Tokens are minted on the real clock: build the target's now, with a lifetime that outlives the jump below.
        var target = TestJwt.Build(name: "Target User", impersonatedBy: Guid.NewGuid().ToString(), lifetime: TimeSpan.FromMinutes(75));
        var release = Http.OnGated(HttpMethod.Post, RefreshPath, $"{{\"access_token\":\"{TestJwt.Build(name: "Staff Renewed", lifetime: TimeSpan.FromHours(2))}\"}}");
        Time.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(1));
        await WaitUntil(() => Refreshes == 2);

        Auth.BeginImpersonation(target);
        release();
        await Task.Delay(50);

        Assert.True(Auth.IsImpersonating);
        Assert.Equal("Target User", Auth.DisplayName);
        Assert.Equal(0, signedIn);
        Time.Advance(TimeSpan.FromMinutes(10));
        await Auth.GetFreshAccessTokenAsync();
        Assert.Equal(2, Refreshes); // the discarded answer re-armed nothing
    }

    [Fact]
    public async Task ARefreshInFlight_WhenLoggingOut_DoesNotResurrectTheSession()
    {
        // LB-UI-12 (web): the user clicks Sign out while the timer's refresh is on the wire. When its answer
        // lands it must be dropped, not applied — or the session logout just ended comes back, SignedIn fires
        // and the preferences logout wiped are re-applied.
        await SignInAsync(name: "First");
        var signedIn = 0;
        Auth.SignedIn += () => signedIn++;
        Http.On(HttpMethod.Post, "/api/auth/logout");
        var release = Http.OnGated(HttpMethod.Post, RefreshPath, $"{{\"access_token\":\"{TestJwt.Build(name: "Resurrected", lifetime: TimeSpan.FromHours(2))}\"}}");
        Time.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(1));
        await WaitUntil(() => Refreshes == 2);

        await Auth.LogoutAsync();
        release();
        await Task.Delay(50);

        Assert.False(Auth.IsAuthenticated);
        Assert.Null(Auth.AccessToken);
        Assert.Equal(0, signedIn);
        Time.Advance(TimeSpan.FromHours(2));
        Assert.Null(await Auth.GetFreshAccessTokenAsync());
        Assert.Equal(2, Refreshes);
    }

    [Fact]
    public async Task Native_ARefreshInFlight_WhenLoggingOut_DoesNotReSaveTheRotatedToken()
    {
        // LB-UI-12 (native): the discarded answer carried a rotated refresh token; after a logout it must not be
        // written back to the secure store — the store stays empty, as logout left it.
        var store = await StoreHolding("rt-1");
        var auth = NativeAuth(store);
        Http.On(HttpMethod.Post, RefreshPath, $"{{\"access_token\":\"{TestJwt.Build(name: "First")}\",\"refresh_token\":\"rt-2\"}}");
        await auth.InitializeAsync();
        Assert.Equal("rt-2", await store.GetRefreshTokenAsync());
        Http.On(HttpMethod.Post, "/api/auth/logout");
        var release = Http.OnGated(HttpMethod.Post, RefreshPath, $"{{\"access_token\":\"{TestJwt.Build(name: "Resurrected", lifetime: TimeSpan.FromHours(2))}\",\"refresh_token\":\"rt-3\"}}");
        Time.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(1));
        await WaitUntil(() => Refreshes == 2);

        await auth.LogoutAsync();
        release();
        await Task.Delay(50);

        Assert.False(auth.IsAuthenticated);
        Assert.Null(await store.GetRefreshTokenAsync());
    }

    [Fact]
    public async Task ImpersonationExpiry_DoesNotSilentlyRenewIntoTheStaffIdentity()
    {
        // LB-UI-13: at the impersonation token's expiry the client used to renew from the staff cookie, so the
        // admin's next click on the target's page ran as staff, in the staff household. Expiry ENDS the
        // impersonation: no refresh, IdentityChanged raised once, and the layout takes the admin home.
        await SignInAsync(name: "Staff Member");
        Auth.BeginImpersonation(TestJwt.Build(name: "Target User", impersonatedBy: Guid.NewGuid().ToString(), lifetime: TimeSpan.FromMinutes(15)));
        var identityChanged = 0;
        Auth.IdentityChanged += () => identityChanged++;

        Time.Advance(TimeSpan.FromMinutes(16));
        var token = await Auth.GetFreshAccessTokenAsync();
        await Auth.GetFreshAccessTokenAsync(); // a second request must not raise it again

        Assert.Null(token);
        Assert.Equal(1, Refreshes);
        Assert.False(Auth.IsImpersonating);
        Assert.Equal(1, identityChanged);
    }

    [Fact]
    public async Task ImpersonationExpiry_TakesTheAdminHome_OffTheTargetsPage()
    {
        // The layout's half of LB-UI-13: when the impersonation ends by expiry, the admin must not be left on the
        // target's page as themselves — the same reload-home that "Stop" does, so the staff identity is restored
        // on a neutral page. Fires from the expiry itself, not only from the next request.
        await SignInAsync(name: "Staff Member");
        var nav = (Bunit.TestDoubles.BunitNavigationManager)Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("/household");
        Render<MainLayout>(ps => ps.Add(m => m.Body, b => b.AddMarkupContent(0, "<div>the target's page</div>")));
        Auth.BeginImpersonation(TestJwt.Build(name: "Target User", impersonatedBy: Guid.NewGuid().ToString(), lifetime: TimeSpan.FromMinutes(15)));
        var before = nav.History.Count;

        Time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));
        await WaitUntil(() => nav.History.Count > before);

        var since = nav.History.Take(nav.History.Count - before).ToList(); // History is newest-first
        var navigations = string.Join(" | ", since.Select(h => $"{h.Uri} forceLoad={h.Options.ForceLoad}"));
        var home = since.SingleOrDefault(h => h.Options.ForceLoad);
        Assert.True(home is not null, "expected one reload home, got: " + navigations);
        Assert.Equal(new Uri(nav.BaseUri), new Uri(new Uri(nav.BaseUri), home!.Uri));
        Assert.False(Auth.IsImpersonating);
        Assert.Equal(1, Refreshes);
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
    public async Task AHungRefresh_TimesOut_WithinTheGraceWindow()
    {
        // v4 UX-6 (T30, R107): a refresh whose response never comes must give up on its OWN clock — HttpClient's
        // 100 s default outlives the server's 60 s reuse grace, so the retry with the old token would land
        // outside the window and be read as theft (every session revoked). Timeout + retry delay < grace.
        await SignInAsync(name: "First");
        var signedOut = 0;
        Auth.SignedOut += () => signedOut++;
        Http.OnGated(HttpMethod.Post, RefreshPath); // never released: the response is lost

        Time.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(1)); // the renewal fires and hangs
        await WaitUntil(() => Refreshes == 2);
        var sentAt = Time.GetUtcNow();

        Time.Advance(AuthService.RefreshTimeout + TimeSpan.FromSeconds(1)); // the call gives up: unreachable, not rejected
        await AdvanceUntil(() => Refreshes == 3, limit: TimeSpan.FromSeconds(60) - AuthService.RefreshTimeout);

        Assert.Equal(0, signedOut);
        Assert.True(Time.GetUtcNow() - sentAt < TimeSpan.FromSeconds(60), "the retry must land inside the server's grace window");
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "<html>Access denied</html>", true)]      // a proxy's challenge page: not the API's verdict
    [InlineData(HttpStatusCode.Forbidden, """{"error":"invalid_refresh_token"}""", false)]
    [InlineData(HttpStatusCode.BadRequest, "", true)]                              // an empty 400 from something in front of the API
    [InlineData(HttpStatusCode.BadRequest, """{"error":"no_refresh_token"}""", false)]
    [InlineData(HttpStatusCode.Unauthorized, "<html>", false)]                     // 401 is always the verdict
    public async Task Native_RejectionNeedsTheApisOwnErrorBody(HttpStatusCode status, string body, bool keepsToken)
    {
        // v4 UX-7 (T30, R108): a 400/403 from a firewall or proxy in front of the API (an HTML challenge page)
        // is not the server refusing the token — deleting the 30-day native token on it is what made the app
        // sign out for good. Only a body shaped like the API's ErrorResponse counts as a rejection.
        var store = await StoreHolding("rt-1");
        var auth = NativeAuth(store);
        Http.On(HttpMethod.Post, RefreshPath, body, status);

        Assert.False(await auth.TryRefreshAsync());

        Assert.Equal(keepsToken ? "rt-1" : null, await store.GetRefreshTokenAsync());
    }

    [Fact]
    public async Task MidSession_RepeatedFailures_BackOff_UpToACap()
    {
        // v4 UX-12 (T30, R144): a fixed 30 s retry kept a background app waking twice a minute for as long as
        // the server was down. The pause doubles per consecutive failure — 30 s, 1 min, 2 min, 4 min — and is
        // capped; a renewal resets it.
        await SignInAsync(name: "First");
        Http.OnUnreachable(HttpMethod.Post, RefreshPath);
        Time.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(1));
        await WaitUntil(() => Refreshes == 2);

        var gaps = new List<TimeSpan>();
        var previous = Time.GetUtcNow();
        for (var attempt = 3; attempt <= 8; attempt++)
        {
            await AdvanceUntil(() => Refreshes == attempt, limit: AuthService.RenewRetryCap + TimeSpan.FromSeconds(5));
            gaps.Add(Time.GetUtcNow() - previous);
            previous = Time.GetUtcNow();
        }

        // Each gap is (within the 1 s stepping of AdvanceUntil) double the last, until the cap.
        for (var i = 0; i < gaps.Count; i++)
        {
            var expected = TimeSpan.FromTicks(Math.Min(AuthService.RenewRetryDelay.Ticks << i, AuthService.RenewRetryCap.Ticks));
            Assert.InRange(gaps[i], expected, expected + TimeSpan.FromSeconds(2));
        }
        Assert.Contains(gaps, g => g >= AuthService.RenewRetryCap); // the cap was reached inside the loop

        StubRefresh("Renewed", lifetime: TimeSpan.FromHours(2));
        await AdvanceUntil(() => Auth.DisplayName == "Renewed", limit: AuthService.RenewRetryCap + TimeSpan.FromSeconds(5));
        Http.OnUnreachable(HttpMethod.Post, RefreshPath);
        var renewedAt = Time.GetUtcNow();
        var before = Refreshes;
        Time.Advance(TimeSpan.FromHours(2) - AuthService.RenewLead + TimeSpan.FromSeconds(1)); // the next renewal fails again
        await WaitUntil(() => Refreshes == before + 1);
        var failedAt = Time.GetUtcNow();
        await AdvanceUntil(() => Refreshes == before + 2, limit: AuthService.RenewRetryDelay + TimeSpan.FromSeconds(5));
        Assert.InRange(Time.GetUtcNow() - failedAt, AuthService.RenewRetryDelay, AuthService.RenewRetryDelay + TimeSpan.FromSeconds(2)); // back to the base pause
        Assert.True(renewedAt < failedAt);
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
