using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests.Integration;

/// <summary>
/// DISPLAY-1 over HTTP through the real app: anonymous is refused; a member reads the default, saves a choice and
/// reads it back; a bad value is 400 in the shared error shape. (The impersonation refusal rides the shared
/// <c>ImpersonationGuardTests</c> theory.)
/// </summary>
[Collection(IntegrationCollection.Name)]
public class DisplaySettingsEndpointTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public async Task Anonymous_IsRefused_OnReadAndWrite()
    {
        var anon = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/display-settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsJsonAsync("/api/display-settings", new { display_currency = "USD" })).StatusCode);
    }

    [Fact]
    public async Task Member_ReadsTheDefault_SavesAChoice_AndReadsItBack()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);

        var before = await client.GetFromJsonAsync<Dto>("/api/display-settings");
        Assert.Equal(("both", true), (before!.DisplayCurrency, before.IsDefault));

        var put = await client.PutAsJsonAsync("/api/display-settings", new { display_currency = "usd" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var after = await client.GetFromJsonAsync<Dto>("/api/display-settings");
        Assert.Equal(("USD", false), (after!.DisplayCurrency, after.IsDefault));

        var bad = await client.PutAsJsonAsync("/api/display-settings", new { display_currency = "EUR" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("invalid_request", await bad.Content.ReadAsStringAsync());
        Assert.Equal("USD", (await client.GetFromJsonAsync<Dto>("/api/display-settings"))!.DisplayCurrency); // nothing written on a 400
    }

    private sealed record Dto(
        [property: JsonPropertyName("display_currency")] string DisplayCurrency,
        [property: JsonPropertyName("is_default")] bool IsDefault);
}
