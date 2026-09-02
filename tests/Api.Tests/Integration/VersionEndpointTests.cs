using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Perezosoft.Api.Tests.Infrastructure;

namespace Perezosoft.Api.Tests.Integration;

/// <summary>
/// The anonymous <c>GET /api/version</c> build-identity endpoint (DEPLOY-3). The post-deploy smoke polls
/// it to confirm the NEW build is live before asserting. Here we just prove it's reachable without a
/// token and reports a commit field ("unknown" when no build env var is set, as in tests).
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
    }

    private sealed record VersionResponse([property: JsonPropertyName("commit")] string Commit);
}
