using Vuelto.Shared.Ui.Layout;
using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Perezosoft.Client;
using Vuelto.Shared.Ui;
using Perezosoft.Client.Auth;
using Vuelto.Ui.Tests.Infrastructure;
using Perezosoft.Testing.Ui;
using Xunit;
using Vuelto.Shared.Ui.Auth;

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

    private sealed class BearerRetryHandler(AuthService auth) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            BearerRetry.SendAsync(auth, request, base.SendAsync, cancellationToken);
    }

    // ── a refresh in flight across a change of session is discarded (v4 T31, R125) ──

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

    /// <summary>Waits in REAL time (up to 10 s) for the async work the fake clock woke. Two seconds was too tight for a
    /// loaded CI runner, where xUnit's parallel classes starve the continuations (jigger-jot CI, 2026-10-08).</summary>
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }
    [Fact]
    public async Task Join_RefreshAfterAccept_DoesNotReuseARenewalStartedBeforeTheAccept()
    {
        // The page half of LB-UI-16: after accepting an invitation, Join refreshes so the new household shows;
        // reusing a renewal that started before the accept left the user with a token scoped to the household
        // they just left — for up to an hour, every page looked empty.
        await SignInAsync(name: "First", tenantName: "Old Household");
        var release = Http.OnGated(HttpMethod.Post, RefreshPath,
            $"{{\"access_token\":\"{TestJwt.Build(name: "First", tenantName: "Old Household", lifetime: TimeSpan.FromHours(2))}\"}}");
        Time.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(1));
        await WaitUntil(() => Refreshes == 2);
        Http.On(HttpMethod.Post, "/api/household/invitations/accept", "{}");
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("/join?token=invite-token");

        var page = Render<Vuelto.Shared.Ui.Pages.Join>();
        await Task.Delay(50); // the accept POST ran; the page is waiting on its refresh
        Http.On(HttpMethod.Post, RefreshPath, $"{{\"access_token\":\"{TestJwt.Build(name: "First", tenantName: "New Household", lifetime: TimeSpan.FromHours(2))}\"}}");
        release();
        await WaitUntil(() => Auth.TenantName == "New Household");

        Assert.Equal(3, Refreshes);
        page.WaitForAssertion(() => Assert.Contains("New Household", Auth.TenantName));
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

    [Fact]
    public async Task UserSignOut_TheLayoutLeavesTheNavigationToTheCaller()
    {
        // The sign-out button logs out and then leaves for /login with a full reload. The layout's redirect is for a
        // session the user did not end; on their own sign-out it raced the reload and could replace the history entry
        // being left, so Back no longer reached the page the bfcache guard protects
        // (SignOut_ThenBack_LandsOnLogin_NotTheCachedHousehold failed whenever the redirect won).
        await SignInAsync(name: "First", theme: "dark");
        var nav = (Bunit.TestDoubles.BunitNavigationManager)Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("/household");
        Render<MainLayout>(ps => ps.Add(m => m.Body, b => b.AddMarkupContent(0, "<div>page</div>")));
        Http.On(HttpMethod.Post, "/api/auth/logout");

        await Auth.LogoutAsync();
        await Task.Delay(50);

        Assert.True(ThemeStore.Cleared);          // the device-preference wipe still runs
        Assert.EndsWith("/household", nav.Uri);   // the layout did not navigate; the caller's reload does
        Assert.False(Auth.SigningOut);
    }

    // ── the token's lifetime is the server's, not the device clock's (v4 T32, R126) ──

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

    [Fact]
    public async Task Resume_RenewsASessionWhoseRenewalFailedWhileAway()
    {
        await SignInAsync(name: "First");
        Render<MainLayout>(ps => ps.Add(m => m.Body, b => b.AddMarkupContent(0, "<div>body</div>")));

        // The renewal ran while the phone was in a pocket with no signal, and its retry hasn't come round.
        Http.OnUnreachable(HttpMethod.Post, RefreshPath);
        var armed = Time.ArmedCount;
        Time.Advance(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(1));
        // Wait for that renewal to SETTLE, not just to be sent (#381): a refresh in flight is shared, so a resume that
        // lands while the failure is still being handled joins it instead of making its own attempt — right for the
        // app, and on a loaded runner exactly what made this test time out. Settled = its retry is armed.
        await WaitUntil(() => Refreshes == 2 && Time.ArmedSince(armed).Contains(AuthService.RenewRetryDelay));
        Time.Advance(TimeSpan.FromSeconds(20));

        StubRefresh("Renewed");
        Services.GetRequiredService<AppResumeNotifier>().Notify();

        await WaitUntil(() => Auth.DisplayName == "Renewed");
        Assert.Equal(3, Refreshes);
    }
}
