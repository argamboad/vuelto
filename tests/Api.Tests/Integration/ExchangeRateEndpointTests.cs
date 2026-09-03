using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests.Integration;

/// <summary>
/// FX-1 over HTTP through the real app: anonymous is refused by the group policy; a member gets either
/// a resolved rate (200, when the developer's .env carries a provider key — the harness boots the real
/// config) or the honest 503 <c>exchange_rate_unavailable</c> in the shared error shape. Either way the
/// contract is what the Postman request documents; the chain itself is proven in the unit tests.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class ExchangeRateEndpointTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public async Task Anonymous_IsRefused()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/exchange-rate")).StatusCode);
    }

    [Fact]
    public async Task Member_GetsARate_OrTheHonest503()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);

        var res = await client.GetAsync("/api/exchange-rate");

        if (res.StatusCode == HttpStatusCode.OK)
        {
            var rate = await res.Content.ReadFromJsonAsync<RateDto>();
            Assert.True(rate!.Rate > 0);
            Assert.Contains(rate.Source, new[] { "live", "cache" }); // no transactions exist yet
            return;
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        var error = await res.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.Equal("exchange_rate_unavailable", error!.Error);
        Assert.Contains("try again later", error.Message);
    }

    private sealed record RateDto(
        [property: JsonPropertyName("rate")] decimal Rate,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("as_of")] DateTimeOffset AsOf);

    private sealed record ErrorDto(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("message")] string Message);
}
