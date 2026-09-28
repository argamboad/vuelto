using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Api.Tests.Integration;

/// <summary>
/// v3 audit TB-AUTH-1 (T44): the refresh-token theft response, proven at the wire. The service-level
/// pieces (rotation, reuse classification, revoke-all) are unit-covered in
/// <see cref="RefreshTokenServiceTests"/>; this asserts the CONTROLLER actually mounts the response —
/// replaying a rotated-out token through <c>POST /api/auth/refresh</c> must (a) return the same generic
/// 401 as any bad token (no reuse signal leaked to the attacker) and (b) revoke every live session for
/// the user, killing the legitimately-rotated token too.
/// ADR-002 addendum (2026-09-18): a replay WITHIN <c>RefreshToken:ReuseGraceSeconds</c> (60 s) of the
/// rotation, while the successor is still live, is a benign race (two tabs, a lost response) — it gets
/// a fresh session and revokes nothing. The theft tests therefore move the rotation out of the window
/// first. Most tests use the native (body-token) transport so they drive raw tokens without a cookie
/// jar; the two-tab race uses the web cookie transport, where it actually happens.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class RefreshReplayTests(IntegrationTestFactory factory)
{
    private const string RefreshCookie = "refresh_token";

    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public async Task Refresh_RotatedTokenReplay_AfterGraceWindow_RevokesAllSessions_WithoutLeakingReuse()
    {
        var user = await _factory.SeedUserAsync();
        var rawA = await IssueRefreshTokenAsync(user.UserId);
        var phone = await IssueRefreshTokenAsync(user.UserId);
        var client = _factory.CreateClient();

        // 1. Legit rotation: A → B (A is revoked server-side, B comes back on the body).
        var first = await PostRefreshAsync(client, rawA);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var rawB = await ReadRefreshTokenAsync(first);
        Assert.False(string.IsNullOrEmpty(rawB));

        // The rotation is now older than the 60 s grace window.
        await AgeRotationsAsync(user.UserId, TimeSpan.FromSeconds(61));

        // 2. Attacker replays the rotated-out A. Must be indistinguishable from a plain bad token —
        //    same status AND same error code as a never-issued garbage token.
        var replay = await PostRefreshAsync(client, rawA);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        var replayBody = await replay.Content.ReadAsStringAsync();
        var garbage = await PostRefreshAsync(client, "never-issued-token");
        Assert.Equal(HttpStatusCode.Unauthorized, garbage.StatusCode);
        Assert.Equal(await garbage.Content.ReadAsStringAsync(), replayBody);

        // 3. The theft response revoked EVERY session: the legitimately-rotated B is dead too, and so is
        //    the user's other device.
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefreshAsync(client, rawB!)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefreshAsync(client, phone)).StatusCode);
        Assert.Equal(0, await CountLiveTokensAsync(user.UserId));
    }

    [Fact]
    public async Task Refresh_ValidToken_RotatesAndKeepsOtherSessionsAlive()
    {
        // The theft response must be replay-triggered only: a NORMAL rotation must not touch the
        // user's other sessions (e.g. their phone stays signed in when the laptop refreshes).
        var user = await _factory.SeedUserAsync();
        var laptop = await IssueRefreshTokenAsync(user.UserId);
        var phone = await IssueRefreshTokenAsync(user.UserId);
        var client = _factory.CreateClient();

        var refreshed = await PostRefreshAsync(client, laptop);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        var phoneRefresh = await PostRefreshAsync(client, phone);
        Assert.Equal(HttpStatusCode.OK, phoneRefresh.StatusCode);
    }

    [Fact]
    public async Task Refresh_SameCookieTwice_BackToBack_BothSucceed_AndNothingIsRevoked()
    {
        // The staging bug: two tabs refresh at once with the same cookie (or a refresh's response is lost
        // and the browser retries with the old cookie). The second presentation lands inside the grace
        // window with a live successor — a race, not a theft: both get a session, no one is signed out.
        var user = await _factory.SeedUserAsync();
        var cookie = await IssueRefreshTokenAsync(user.UserId);
        var phone = await IssueRefreshTokenAsync(user.UserId);
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        var tab1 = await PostWebRefreshAsync(client, cookie);
        var tab2 = await PostWebRefreshAsync(client, cookie);

        Assert.Equal(HttpStatusCode.OK, tab1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, tab2.StatusCode);
        var rotated1 = ReadRefreshCookie(tab1);
        var rotated2 = ReadRefreshCookie(tab2);
        Assert.False(string.IsNullOrEmpty(rotated1));
        Assert.False(string.IsNullOrEmpty(rotated2));
        Assert.NotEqual(rotated1, rotated2);

        // Nothing was revoked: both new chains and the user's other device keep working.
        Assert.Equal(HttpStatusCode.OK, (await PostRefreshAsync(client, phone)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostWebRefreshAsync(client, rotated1!)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostWebRefreshAsync(client, rotated2!)).StatusCode);
    }

    [Fact]
    public async Task Refresh_ThirdPresentationInsideGrace_Is401_AndRevokesAllSessions()
    {
        // v4 AUTH-1 (T28, R81): the grace is one-shot. A rotated-out token forgiven once (the benign race)
        // must not be forgiven again — a third presentation inside the same 60 s window is reuse: the
        // generic 401, and every session of the user revoked (both chains the grace created, and the phone).
        var user = await _factory.SeedUserAsync();
        var rawA = await IssueRefreshTokenAsync(user.UserId);
        var phone = await IssueRefreshTokenAsync(user.UserId);
        var client = _factory.CreateClient();

        var first = await PostRefreshAsync(client, rawA);                       // A → B
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await PostRefreshAsync(client, rawA);                      // A again, inside the grace: forgiven → C
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var rawC = await ReadRefreshTokenAsync(second);

        var third = await PostRefreshAsync(client, rawA);                       // A a third time: reuse

        Assert.Equal(HttpStatusCode.Unauthorized, third.StatusCode);
        Assert.Equal(await (await PostRefreshAsync(client, "never-issued-token")).Content.ReadAsStringAsync(),
            await third.Content.ReadAsStringAsync());
        Assert.Equal(0, await CountLiveTokensAsync(user.UserId));
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefreshAsync(client, rawC!)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefreshAsync(client, phone)).StatusCode);
    }

    [Fact]
    public async Task Refresh_AttackerRotatesFirst_VictimsRenewalInsideGrace_GetsASession_ByDesign()
    {
        // v4 AUTH-13 (T28): the grace is symmetric, and this test records the accepted trade-off rather than
        // hides it (ADR-002 addendum, 2026-09-28). A thief holding a copy of A who rotates it FIRST gets B; the
        // victim's own renewal with A, landing inside the window, is the "benign race" and gets C — the alarm
        // does not fire for that pair. What the platform guarantees instead: the grace was spent on the
        // victim's renewal, so the thief's NEXT replay of A (or the victim's, whichever comes) revokes everything,
        // and the window is bounded by the client's refresh timeout + retry delay (T30) being shorter than it.
        var user = await _factory.SeedUserAsync();
        var rawA = await IssueRefreshTokenAsync(user.UserId);
        var client = _factory.CreateClient();

        var thief = await PostRefreshAsync(client, rawA);                       // attacker first: A → B
        Assert.Equal(HttpStatusCode.OK, thief.StatusCode);
        var rawB = await ReadRefreshTokenAsync(thief);
        var victim = await PostRefreshAsync(client, rawA);                      // victim's scheduled renewal
        Assert.Equal(HttpStatusCode.OK, victim.StatusCode);                     // forgiven — the documented trade
        Assert.Equal(2, await CountLiveTokensAsync(user.UserId));               // both chains live

        var replay = await PostRefreshAsync(client, rawA);                      // anyone presents A once more

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(0, await CountLiveTokensAsync(user.UserId));               // the thief's B is dead too
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostRefreshAsync(client, rawB!)).StatusCode);
    }

    [Fact]
    public async Task Refresh_PreLogoutRotatedToken_WithinGraceWindow_Is401_AndIssuesNoSession()
    {
        // Logout revokes the successor, so a stale token from another tab can never undo a sign-out —
        // even seconds after the rotation, inside the grace window.
        var user = await _factory.SeedUserAsync();
        var rawA = await IssueRefreshTokenAsync(user.UserId);
        var client = _factory.CreateClient();

        var rotated = await PostRefreshAsync(client, rawA);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var rawB = await ReadRefreshTokenAsync(rotated);

        var logout = await PostNativeAsync(client, "/api/auth/logout", rawB!);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        var tokensAfterLogout = await CountTokensAsync(user.UserId);

        var stale = await PostRefreshAsync(client, rawA);

        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Equal(0, await CountLiveTokensAsync(user.UserId));
        Assert.Equal(tokensAfterLogout, await CountTokensAsync(user.UserId)); // no session was minted
    }

    /// <summary>Issues a session for the user via the app's own service — the same path a login uses.</summary>
    private async Task<string> IssueRefreshTokenAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        return (await service.IssueRefreshTokenAsync(userId, "127.0.0.1", "test")).RawToken;
    }

    /// <summary>Moves every rotation of the user's tokens <paramref name="age"/> into the past (the app runs on the real clock).</summary>
    private async Task AgeRotationsAsync(Guid userId, TimeSpan age)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var aged = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RotatedAt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RotatedAt, t => t.RotatedAt - age));
        Assert.True(aged > 0, "expected at least one rotated token to age");
    }

    private async Task<int> CountLiveTokensAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.RefreshTokens.CountAsync(t => t.UserId == userId && !t.IsRevoked);
    }

    private async Task<int> CountTokensAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.RefreshTokens.CountAsync(t => t.UserId == userId);
    }

    private static Task<HttpResponseMessage> PostRefreshAsync(HttpClient client, string rawToken) =>
        PostNativeAsync(client, "/api/auth/refresh", rawToken);

    private static Task<HttpResponseMessage> PostNativeAsync(HttpClient client, string path, string rawToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { refresh_token = rawToken }),
        };
        req.Headers.Add(AuthHeaders.NativeClient, AuthHeaders.NativeClientValue);
        return client.SendAsync(req);
    }

    /// <summary>The browser transport: the refresh token rides the HttpOnly cookie, the body is empty.</summary>
    private static Task<HttpResponseMessage> PostWebRefreshAsync(HttpClient client, string rawToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh");
        // Encoded the way CookieService writes it (base64 carries + / =), as the browser sends it back.
        req.Headers.Add("Cookie", $"{RefreshCookie}={Uri.EscapeDataString(rawToken)}");
        return client.SendAsync(req);
    }

    private static async Task<string?> ReadRefreshTokenAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("refresh_token").GetString();
    }

    /// <summary>The value of the Path=/api/auth refresh cookie set on the response (the Path=/ one is the legacy-orphan expiry).</summary>
    private static string? ReadRefreshCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            return null;
        var cookie = setCookies.FirstOrDefault(c =>
            c.StartsWith($"{RefreshCookie}=", StringComparison.Ordinal) &&
            c.Contains("path=/api/auth", StringComparison.OrdinalIgnoreCase));
        return cookie is null ? null : Uri.UnescapeDataString(cookie[(RefreshCookie.Length + 1)..].Split(';')[0]);
    }
}
