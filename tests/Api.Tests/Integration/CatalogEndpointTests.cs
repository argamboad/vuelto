using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests.Integration;

/// <summary>CATALOG-1/2 over HTTP (RLS enforced): 401 anonymous, 201/200 for a member, the 409 offer shape, both prefixes.</summary>
[Collection(IntegrationCollection.Name)]
public class CatalogEndpointTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Theory]
    [InlineData("/api/categories")]
    [InlineData("/api/banks")]
    public async Task Anonymous_IsRefused(string prefix)
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(prefix)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync(prefix, new { name = "x" })).StatusCode);
    }

    [Fact]
    public async Task Member_ListsSeededCategories_CreatesOne_AndGetsTheReactivationOffer()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);

        var seeded = await client.GetFromJsonAsync<List<EntryDto>>("/api/categories");
        Assert.Equal(7, seeded!.Count);              // first read seeded the defaults

        var created = await client.PostAsJsonAsync("/api/categories", new { name = "Viajes" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var entry = await created.Content.ReadFromJsonAsync<EntryDto>();

        var deactivated = await client.PutAsJsonAsync($"/api/categories/{entry!.Id}", new { name = "Viajes", is_active = false });
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);

        var clash = await client.PostAsJsonAsync("/api/categories", new { name = "viajes" });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        var offer = await clash.Content.ReadFromJsonAsync<ConflictDto>();
        Assert.Equal("category_exists_inactive", offer!.Error);
        Assert.Equal(entry.Id, offer.ExistingId);
        Assert.Equal("Viajes", offer.ExistingName);

        var withInactive = await client.GetFromJsonAsync<List<EntryDto>>("/api/categories?include_inactive=true");
        Assert.Contains(withInactive!, c => c.Name == "Viajes" && !c.IsActive);
    }

    [Fact]
    public async Task Banks_SeedCash_AndForeignIdIs404()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);

        var banks = await client.GetFromJsonAsync<List<EntryDto>>("/api/banks");
        Assert.Contains(banks!, b => b.Name == "Cash");

        var res = await client.PutAsJsonAsync($"/api/banks/{Guid.CreateVersion7()}", new { name = "Nope", is_active = true });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    private sealed record EntryDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("is_active")] bool IsActive);

    private sealed record ConflictDto(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("existing_id")] Guid? ExistingId,
        [property: JsonPropertyName("existing_name")] string? ExistingName);
}
