using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests.Integration;

/// <summary>EXPENSES-1 over HTTP (RLS enforced): 401 anonymous; create/list/update/reorder on both lists; 400 shape; 409 offer; 404.</summary>
[Collection(IntegrationCollection.Name)]
public class ExpenseEndpointTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Theory]
    [InlineData("/api/expenses/fixed")]
    [InlineData("/api/expenses/variable")]
    public async Task Anonymous_IsRefused(string prefix)
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(prefix)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsJsonAsync($"{prefix}/order", new { ordered_ids = new Guid[0] })).StatusCode);
    }

    [Fact]
    public async Task Member_BuildsAndReordersTheCatalog()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);
        var categories = (await client.GetFromJsonAsync<List<NamedDto>>("/api/categories"))!; // first read seeds them
        var bank = (await client.GetFromJsonAsync<List<NamedDto>>("/api/banks"))![0];

        Assert.Empty((await client.GetFromJsonAsync<List<LineDto>>("/api/expenses/fixed"))!); // never seeded

        var first = await client.PostAsJsonAsync("/api/expenses/fixed", new { name = "Mortgage", budget_crc = 300_000m, budget_usd = 0m, payment_method = "bank_account", category_id = categories[0].Id, bank_id = bank.Id });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var mortgage = (await first.Content.ReadFromJsonAsync<LineDto>())!;
        Assert.Equal((0, bank.Id), (mortgage.SortOrder, mortgage.BankId));

        var second = await client.PostAsJsonAsync("/api/expenses/fixed", new { name = "Water", budget_crc = 15_000m, budget_usd = 0m, payment_method = "bank_account", category_id = categories[1].Id });
        var water = (await second.Content.ReadFromJsonAsync<LineDto>())!;
        Assert.Equal(1, water.SortOrder);

        var bothCurrencies = await client.PostAsJsonAsync("/api/expenses/variable", new { name = "Bad", budget_crc = 1m, budget_usd = 1m, payment_method = "credit_card", category_id = categories[2].Id });
        Assert.Equal(HttpStatusCode.BadRequest, bothCurrencies.StatusCode);
        Assert.Equal("invalid_request", (await bothCurrencies.Content.ReadFromJsonAsync<ConflictDto>())!.Error);

        var takenCategory = await client.PostAsJsonAsync("/api/expenses/variable", new { name = "Groceries", budget_crc = 200_000m, budget_usd = 0m, payment_method = "credit_card", category_id = categories[0].Id });
        Assert.Equal(HttpStatusCode.BadRequest, takenCategory.StatusCode); // Mortgage's category, across lists

        var clash = await client.PostAsJsonAsync("/api/expenses/fixed", new { name = "MORTGAGE", budget_crc = 1m, budget_usd = 0m, payment_method = "bank_account", category_id = categories[2].Id });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal("expense_exists", (await clash.Content.ReadFromJsonAsync<ConflictDto>())!.Error);

        var reorder = await client.PutAsJsonAsync("/api/expenses/fixed/order", new { ordered_ids = new[] { water.Id, mortgage.Id } });
        Assert.Equal(HttpStatusCode.NoContent, reorder.StatusCode);
        var list = (await client.GetFromJsonAsync<List<LineDto>>("/api/expenses/fixed"))!;
        Assert.Equal(new[] { water.Id, mortgage.Id }, list.Select(l => l.Id));

        var deactivate = await client.PutAsJsonAsync($"/api/expenses/fixed/{water.Id}", new { name = "Water", budget_crc = 15_000m, budget_usd = 0m, payment_method = "bank_account", category_id = categories[1].Id, is_active = false });
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        Assert.Single((await client.GetFromJsonAsync<List<LineDto>>("/api/expenses/fixed"))!);
        Assert.Equal(2, (await client.GetFromJsonAsync<List<LineDto>>("/api/expenses/fixed?include_inactive=true"))!.Count);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/expenses/variable/{Guid.CreateVersion7()}", new { name = "X", budget_crc = 1m, budget_usd = 0m, payment_method = "credit_card", category_id = categories[2].Id, is_active = true })).StatusCode);
    }

    private sealed record NamedDto([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("name")] string Name);
    private sealed record LineDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("sort_order")] int SortOrder,
        [property: JsonPropertyName("bank_id")] Guid? BankId,
        [property: JsonPropertyName("is_active")] bool IsActive);
    private sealed record ConflictDto([property: JsonPropertyName("error")] string Error, [property: JsonPropertyName("message")] string Message);
}
