using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Vuelto.Api.Tests.Infrastructure;

namespace Vuelto.Api.Tests.Integration;

/// <summary>
/// GATES-1 (ADR-027): the anonymous <c>GET /api/features</c> probe. The Blazor client cannot read server
/// configuration, so this is how it learns whether to render the billing link and let <c>/billing</c>
/// route at all. Anonymous on purpose — the header renders before anything is known about the caller,
/// and "does this deployment sell subscriptions" is not a secret.
/// <para>
/// The client using it is a convenience, never the enforcement: the API stays the authority and the
/// billing routes are gone entirely when the gate is off (see <c>BillingGateTests</c>).
/// </para>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class FeaturesEndpointTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public async Task Features_IsAnonymous_AndReportsBillingOff_ByDefault()
    {
        var res = await _factory.CreateClient().GetAsync("/api/features"); // no token → must still 200

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<FeaturesResponse>();
        Assert.NotNull(body);
        Assert.False(body!.Billing); // shipped default: a fresh deployment sells nobody anything
    }

    [Fact]
    public async Task Features_ReportsBillingOn_WhenTheGateIsOpen()
    {
        var client = _factory.WithWebHostBuilder(b => b.UseSetting("Billing:Enabled", "true")).CreateClient();

        var body = await client.GetFromJsonAsync<FeaturesResponse>("/api/features");

        Assert.True(body!.Billing);
    }

    private sealed record FeaturesResponse(
        [property: JsonPropertyName("billing")] bool Billing);
}
