using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests.Integration;

/// <summary>LEDGER-1/2 over HTTP through the real app (RLS enforced): 401 anonymous; the full create → month → list → delete → month-gone loop; resolve; 400 shapes; uniform 404.</summary>
[Collection(IntegrationCollection.Name)]
public class LedgerEndpointTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public async Task Anonymous_IsRefused()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/months")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/months/resolve?date=2026-06-05")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/transactions", new { payee = "x" })).StatusCode);
    }

    [Fact]
    public async Task Member_CreatesATransaction_WhichCreatesTheMonth_AndDeletingItRemovesTheMonth()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);
        var category = (await client.GetFromJsonAsync<List<NamedDto>>("/api/categories"))![0]; // first read seeds the catalog
        var bank = (await client.GetFromJsonAsync<List<NamedDto>>("/api/banks"))![0];

        Assert.Empty((await client.GetFromJsonAsync<List<MonthDto>>("/api/months"))!);
        var resolve = await client.GetFromJsonAsync<ResolveDto>("/api/months/resolve?date=2026-07-10");
        Assert.Equal((2026, 7, true), (resolve!.Year, resolve.MonthNumber, resolve.IsNew));

        var created = await client.PostAsJsonAsync("/api/transactions", new
        {
            payee = "AutoMercado", bank_id = bank.Id, payment_method = "credit_card", original_amount = 50_000m, currency = "CRC",
            transaction_date = "2026-07-10", category_id = category.Id, transaction_type = "budgeted", exchange_rate = 500m,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var tx = (await created.Content.ReadFromJsonAsync<TxDto>())!;
        Assert.Equal((500m, 50_000m, 100m, "manual"), (tx.ExchangeRateUsed, tx.AmountCrc, tx.AmountUsd, tx.Source));

        var months = (await client.GetFromJsonAsync<List<MonthDto>>("/api/months"))!;
        var month = Assert.Single(months);
        Assert.Equal((tx.MonthId, 2026, 7, 5), (month.Id, month.Year, month.MonthNumber, month.WeekCount));

        var detail = (await client.GetFromJsonAsync<MonthDto>($"/api/months/{month.Id}"))!;
        Assert.Equal(5, detail.Weeks!.Count);

        var rows = (await client.GetFromJsonAsync<List<RowDto>>($"/api/months/{month.Id}/transactions"))!;
        Assert.Equal(("AutoMercado", category.Name, bank.Name), (Assert.Single(rows).Payee, rows[0].CategoryName, rows[0].BankName));

        var income = await client.PutAsJsonAsync($"/api/months/{month.Id}/income", new { primary_income_amount = 3750m, primary_income_currency = "USD", secondary_income_amount = 0m, secondary_income_currency = "CRC" });
        Assert.Equal(HttpStatusCode.OK, income.StatusCode);

        var invalid = await client.PostAsJsonAsync("/api/transactions", new { payee = "x", bank_id = bank.Id, original_amount = 1m, currency = "EUR", transaction_date = "2026-07-10", category_id = category.Id, transaction_type = "budgeted", exchange_rate = 500m });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("invalid_request", (await invalid.Content.ReadFromJsonAsync<ErrorDto>())!.Error);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/transactions/{tx.Id}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<MonthDto>>("/api/months"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/months/{month.Id}")).StatusCode);
    }

    [Fact]
    public async Task ForeignOrUnknownIds_Are404()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);
        var id = Guid.CreateVersion7();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/transactions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/transactions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/months/{id}/transactions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/months/{id}/income", new { primary_income_amount = 1m, primary_income_currency = "USD", secondary_income_amount = 0m, secondary_income_currency = "USD" })).StatusCode);
    }

    private sealed record NamedDto([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("name")] string Name);
    private sealed record WeekDto([property: JsonPropertyName("week_number")] int WeekNumber);
    private sealed record MonthDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("year")] int Year,
        [property: JsonPropertyName("month_number")] int MonthNumber,
        [property: JsonPropertyName("week_count")] int WeekCount,
        [property: JsonPropertyName("weeks")] List<WeekDto>? Weeks);
    private sealed record ResolveDto([property: JsonPropertyName("year")] int Year, [property: JsonPropertyName("month_number")] int MonthNumber, [property: JsonPropertyName("is_new")] bool IsNew);
    private sealed record TxDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("month_id")] Guid MonthId,
        [property: JsonPropertyName("amount_crc")] decimal AmountCrc,
        [property: JsonPropertyName("amount_usd")] decimal AmountUsd,
        [property: JsonPropertyName("exchange_rate_used")] decimal ExchangeRateUsed,
        [property: JsonPropertyName("source")] string Source);
    private sealed record RowDto([property: JsonPropertyName("payee")] string Payee, [property: JsonPropertyName("category_name")] string? CategoryName, [property: JsonPropertyName("bank_name")] string? BankName);
    private sealed record ErrorDto([property: JsonPropertyName("error")] string Error, [property: JsonPropertyName("message")] string Message);
}
