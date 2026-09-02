using System.Net;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// v3 audit TB-UI backfill (T45c) — the AuthService contracts the whole client hangs off:
/// SignedIn/SignedOut fire on TRANSITIONS only (MainLayout reconciles on them), concurrent refreshes
/// coalesce into one HTTP call (the endpoint rotates the token, so a second in-flight call would fail
/// with a revoked token), claim reads never throw on a hostile token, impersonation is claim-driven,
/// and the staff probe caches per identity without caching transient failures.
/// </summary>
public class AuthServiceTests : ComponentTestBase
{
    [Fact]
    public async Task SignedIn_FiresOnTransitionOnly_NotOnRotation()
    {
        var fired = 0;
        Auth.SignedIn += () => fired++;

        await SignInAsync();                 // unauthenticated → authenticated
        Assert.Equal(1, fired);

        Assert.True(await Auth.TryRefreshAsync()); // mid-session rotation: still signed in
        Assert.Equal(1, fired);                    // → no second event
    }

    [Fact]
    public async Task SignedOut_FiresOnce_NotWhenAlreadySignedOut()
    {
        var fired = 0;
        Auth.SignedOut += () => fired++;
        Http.On(HttpMethod.Post, "/api/auth/logout");

        await Auth.LogoutAsync();            // never signed in → no transition
        Assert.Equal(0, fired);

        await SignInAsync();
        await Auth.LogoutAsync();
        Assert.Equal(1, fired);

        await Auth.LogoutAsync();            // already signed out → idempotent
        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task ConcurrentRefreshes_ShareOneHttpCall()
    {
        var release = Http.OnGated(HttpMethod.Post, "/api/auth/refresh",
            $"{{\"access_token\":\"{TestJwt.Build()}\"}}");

        var first = Auth.TryRefreshAsync();
        var second = Auth.TryRefreshAsync(); // overlaps the in-flight call → must coalesce
        release();

        Assert.True(await first);
        Assert.True(await second);
        Assert.Equal(1, Http.Requests.Count(r => r.RequestUri!.AbsolutePath == "/api/auth/refresh"));

        // Only CONCURRENT calls coalesce — a later refresh runs fresh.
        Http.On(HttpMethod.Post, "/api/auth/refresh", $"{{\"access_token\":\"{TestJwt.Build()}\"}}");
        Assert.True(await Auth.TryRefreshAsync());
        Assert.Equal(2, Http.Requests.Count(r => r.RequestUri!.AbsolutePath == "/api/auth/refresh"));
    }

    [Fact]
    public void HostileToken_ReadsAsSignedOut_NeverThrows()
    {
        // BeginImpersonation is the one seam that accepts a raw token — feed it garbage: every
        // claim-derived property must degrade to null/false rather than throw.
        Auth.BeginImpersonation("not-a-jwt-at-all");

        Assert.False(Auth.IsAuthenticated);
        Assert.False(Auth.IsImpersonating);
        Assert.Null(Auth.UserId);
        Assert.Null(Auth.DisplayName);
        Assert.Null(Auth.TenantName);
        Assert.Null(Auth.Locale);
        Assert.Null(Auth.Theme);
    }

    [Fact]
    public void ExpiredToken_ReadsAsSignedOut()
    {
        Auth.BeginImpersonation(TestJwt.Build(lifetime: TimeSpan.FromMinutes(-5)));

        Assert.False(Auth.IsAuthenticated);
        Assert.Null(Auth.UserId);
        Assert.Null(Auth.Theme); // claims of an expired token are not served
    }

    [Fact]
    public async Task Impersonation_IsClaimDriven_AndStopRestoresTheStaffIdentity()
    {
        await SignInAsync(name: "Staff Member");
        Assert.False(Auth.IsImpersonating);

        var signedInFires = 0;
        Auth.SignedIn += () => signedInFires++;

        // Entering an impersonated session must NOT raise SignedIn — MainLayout would otherwise
        // reconcile the impersonated user's preferences into the admin's device (ADM-8/9).
        Auth.BeginImpersonation(TestJwt.Build(name: "Target User", impersonatedBy: Guid.NewGuid().ToString()));
        Assert.True(Auth.IsImpersonating);
        Assert.Equal("Target User", Auth.DisplayName);
        Assert.Equal(0, signedInFires);

        // Stopping restores the staff identity via a REAL refresh (the stub SignInAsync registered) —
        // not a replay of a stale cached task (the v3 T45c synchronous-completion bug).
        Assert.True(await Auth.StopImpersonationAsync());
        Assert.False(Auth.IsImpersonating);
        Assert.Equal("Staff Member", Auth.DisplayName);
        Assert.Equal(2, Http.Requests.Count(r => r.RequestUri!.AbsolutePath == "/api/auth/refresh"));
    }

    [Fact]
    public async Task StaffProbe_CachesPerIdentity_ImpersonationIsNeverStaff()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/admin/me", """{"is_staff":true}""");

        Assert.True(await Auth.IsStaffAsync());
        Assert.True(await Auth.IsStaffAsync()); // cached — nav re-renders must not re-hit the API
        Assert.Equal(1, Http.Requests.Count(r => r.RequestUri!.AbsolutePath == "/api/admin/me"));

        // An impersonated session never advertises staff powers, and doesn't even probe.
        Auth.BeginImpersonation(TestJwt.Build(impersonatedBy: Guid.NewGuid().ToString()));
        Assert.False(await Auth.IsStaffAsync());
        Assert.Equal(1, Http.Requests.Count(r => r.RequestUri!.AbsolutePath == "/api/admin/me"));
    }

    [Fact]
    public async Task StaffProbe_TransientFailure_IsNotCached()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/admin/me", """{"error":"boom"}""", HttpStatusCode.InternalServerError);

        Assert.False(await Auth.IsStaffAsync()); // fails closed…
        Http.On(HttpMethod.Get, "/api/admin/me", """{"is_staff":true}""");
        Assert.True(await Auth.IsStaffAsync()); // …but does NOT cache the failure
        Assert.Equal(2, Http.Requests.Count(r => r.RequestUri!.AbsolutePath == "/api/admin/me"));
    }
}
