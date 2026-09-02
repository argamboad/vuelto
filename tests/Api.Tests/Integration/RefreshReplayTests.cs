using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;

namespace Vuelto.Api.Tests.Integration;

/// <summary>
/// v3 audit TB-AUTH-1 (T44): the refresh-token theft response, proven at the wire. The service-level
/// pieces (rotation, reuse classification, revoke-all) are unit-covered in
/// <see cref="RefreshTokenServiceTests"/>; this asserts the CONTROLLER actually mounts the response —
/// replaying a rotated-out token through <c>POST /api/auth/refresh</c> must (a) return the same generic
/// 401 as any bad token (no reuse signal leaked to the attacker) and (b) revoke every live session for
/// the user, killing the legitimately-rotated token too.
/// Uses the native (body-token) transport so the test drives raw tokens without a cookie jar.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class RefreshReplayTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public async Task Refresh_RotatedTokenReplay_RevokesAllSessions_WithoutLeakingReuse()
    {
        var user = await _factory.SeedUserAsync();
        var rawA = await IssueRefreshTokenAsync(user.UserId);
        var client = _factory.CreateClient();

        // 1. Legit rotation: A → B (A is revoked server-side, B comes back on the body).
        var first = await PostRefreshAsync(client, rawA);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var rawB = await ReadRefreshTokenAsync(first);
        Assert.False(string.IsNullOrEmpty(rawB));

        // 2. Attacker replays the rotated-out A. Must be indistinguishable from a plain bad token —
        //    same status AND same error code as a never-issued garbage token.
        var replay = await PostRefreshAsync(client, rawA);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        var replayBody = await replay.Content.ReadAsStringAsync();
        var garbage = await PostRefreshAsync(client, "never-issued-token");
        Assert.Equal(HttpStatusCode.Unauthorized, garbage.StatusCode);
        Assert.Equal(await garbage.Content.ReadAsStringAsync(), replayBody);

        // 3. The theft response revoked EVERY session: the legitimately-rotated B is dead too.
        var legit = await PostRefreshAsync(client, rawB!);
        Assert.Equal(HttpStatusCode.Unauthorized, legit.StatusCode);
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

    /// <summary>Issues a session for the user via the app's own service — the same path a login uses.</summary>
    private async Task<string> IssueRefreshTokenAsync(Guid userId)
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IRefreshTokenService>();
        return (await service.IssueRefreshTokenAsync(userId, "127.0.0.1", "test")).RawToken;
    }

    private static Task<HttpResponseMessage> PostRefreshAsync(HttpClient client, string rawToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh")
        {
            Content = JsonContent.Create(new { refresh_token = rawToken }),
        };
        req.Headers.Add(AuthHeaders.NativeClient, AuthHeaders.NativeClientValue);
        return client.SendAsync(req);
    }

    private static async Task<string?> ReadRefreshTokenAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("refresh_token").GetString();
    }
}
