using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Vuelto.Api.Tests.Infrastructure;

namespace Vuelto.Api.Tests.Integration;

/// <summary>
/// The anonymous <c>GET /api/version</c> build-identity endpoint (DEPLOY-3). The post-deploy smoke polls
/// it to confirm the NEW build is live before asserting. Here we just prove it's reachable without a
/// token and reports a commit field ("unknown" when no build env var is set, as in tests), and the platform
/// commit it is synced to (Arch A2: "platform" here, a SHA in a downstream app).
/// </summary>
[Collection(IntegrationCollection.Name)]
public class VersionEndpointTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public async Task Version_IsAnonymous_AndReportsACommit()
    {
        var res = await _factory.CreateClient().GetAsync("/api/version"); // no token

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<VersionResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.Commit)); // "unknown" in tests, a SHA when deployed
        // "platform" where the stamp's commit is null (this IS the platform), the stamped SHA in a downstream app.
        Assert.Equal(PlatformStamp.ReadCommit(Path.Combine(AppContext.BaseDirectory, "platform-stamp.json")), body.Platform);
        Assert.NotEqual("unknown", body.Platform); // the stamp travels with the API
    }

    private sealed record VersionResponse([property: JsonPropertyName("commit")] string Commit, [property: JsonPropertyName("platform")] string Platform);
}
