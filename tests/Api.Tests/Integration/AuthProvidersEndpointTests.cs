using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Vuelto.Api.Tests.Infrastructure;

namespace Vuelto.Api.Tests.Integration;

/// <summary>
/// The anonymous <c>GET /api/auth/providers</c> discovery endpoint the login page reads to decide which
/// OAuth buttons to show. Proves it's reachable without a token, returns 200, and reports only valid
/// provider keys (the exact configured/empty behaviour is unit-tested in <see cref="Auth.AuthProvidersTests"/>,
/// since whether OAuth is configured depends on the environment's <c>.env</c>).
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AuthProvidersEndpointTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    private static readonly HashSet<string> Supported = ["google", "microsoft"];

    [Fact]
    public async Task Providers_IsAnonymous_AndReturnsOnlyValidProviderKeys()
    {
        var res = await _factory.CreateClient().GetAsync("/api/auth/providers"); // no token → must still 200

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<ProvidersResponse>();
        Assert.NotNull(body);
        Assert.All(body!.Providers, p => Assert.Contains(p, Supported)); // never an unknown/dead key
        Assert.Equal(body.Providers.Distinct().Count(), body.Providers.Count); // no dupes
    }

    private sealed record ProvidersResponse(
        [property: JsonPropertyName("providers")] IReadOnlyList<string> Providers);
}
