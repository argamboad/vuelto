using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests.Integration;

/// <summary>CARDS-1 over HTTP through the real app (RLS enforced): 401 anonymous; a member creates, lists, renames; a duplicate is 409 in the catalog shape.</summary>
[Collection(IntegrationCollection.Name)]
public class CardEndpointTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public async Task Anonymous_IsRefused()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/cards")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/cards", new { name = "x", last4 = "1234" })).StatusCode);
    }

    [Fact]
    public async Task Member_CreatesListsRenames_AndADuplicateIs409()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);

        Assert.Empty((await client.GetFromJsonAsync<List<CardDto>>("/api/cards"))!); // never seeded

        var created = await client.PostAsJsonAsync("/api/cards", new { name = "Main", brand = "visa", last4 = "************1234" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var card = (await created.Content.ReadFromJsonAsync<CardDto>())!;
        Assert.Equal(("Main", "VISA", "1234", false), (card.Name, card.Brand, card.Last4, card.AutoNamed));

        var dup = await client.PostAsJsonAsync("/api/cards", new { name = "Other", brand = "VISA", last4 = "1234" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Contains("\"existing_id\"", await dup.Content.ReadAsStringAsync());

        var renamed = await client.PutAsJsonAsync($"/api/cards/{card.Id}", new { name = "Allan's card", is_active = true });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Allan's card", Assert.Single((await client.GetFromJsonAsync<List<CardDto>>("/api/cards"))!).Name);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/cards/{Guid.CreateVersion7()}", new { name = "x", is_active = true })).StatusCode);

        // A renewed card merges into the original: one card, two identities.
        var renewed = (await (await client.PostAsJsonAsync("/api/cards", new { name = "VISA-5678", brand = "VISA", last4 = "5678" })).Content.ReadFromJsonAsync<CardDto>())!;
        var merge = await client.PostAsJsonAsync($"/api/cards/{renewed.Id}/merge", new { into = card.Id });
        Assert.Equal(HttpStatusCode.OK, merge.StatusCode);
        var survivor = Assert.Single((await client.GetFromJsonAsync<List<CardDto>>("/api/cards"))!);
        Assert.Equal((card.Id, 2), (survivor.Id, survivor.Identities.Count));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/cards/{card.Id}/merge", new { into = card.Id })).StatusCode);
    }

    private sealed record CardDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("brand")] string Brand,
        [property: JsonPropertyName("last4")] string Last4,
        [property: JsonPropertyName("auto_named")] bool AutoNamed,
        [property: JsonPropertyName("identities")] List<IdentityDto> Identities);
    private sealed record IdentityDto([property: JsonPropertyName("brand")] string Brand, [property: JsonPropertyName("last4")] string Last4);
}
