using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests.Integration;

/// <summary>
/// DASH-1 over HTTP through the real app (RLS enforced): 401 anonymous; a member's first transaction
/// (frozen at 500, no provider key in tests) yields a summary through the chain's last tier with
/// <c>rate_source = transaction</c>; uniform 404 for an unknown month.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class DashboardEndpointTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public async Task Anonymous_IsRefused()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/months/{Guid.CreateVersion7()}/summary")).StatusCode);
    }

    [Fact]
    public async Task Member_ReadsTheDashboardForTheirMonth()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);
        var category = (await client.GetFromJsonAsync<List<NamedDto>>("/api/categories"))![0];
        var bank = (await client.GetFromJsonAsync<List<NamedDto>>("/api/banks"))![0];

        var created = await client.PostAsJsonAsync("/api/transactions", new
        {
            payee = "AutoMercado", bank_id = bank.Id, payment_method = "credit_card", original_amount = 50_000m, currency = "CRC",
            transaction_date = "2026-06-05", category_id = category.Id, transaction_type = "budgeted", exchange_rate = 500m,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var tx = (await created.Content.ReadFromJsonAsync<TxDto>())!;

        var res = await client.GetAsync($"/api/months/{tx.MonthId}/summary");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var dash = (await res.Content.ReadFromJsonAsync<DashDto>())!;

        Assert.Equal((tx.MonthId, 2026, 6, 4), (dash.Month.Id, dash.Month.Year, dash.Month.MonthNumber, dash.Month.WeekCount));
        Assert.Equal((500m, "transaction", false), (dash.ExchangeRate, dash.RateSource, dash.RateUnavailable)); // no provider key in tests → last tier
        Assert.Equal((50_000m, 100m), (dash.Summary!.ExpensesTotal.Crc, dash.Summary.ExpensesTotal.Usd));
        Assert.Equal(category.Name, Assert.Single(dash.Summary.OtherSpending).CategoryName); // no budget line yet
        Assert.Equal(4, dash.Summary.WeeklyBudgeted.Count);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/months/{Guid.CreateVersion7()}/summary")).StatusCode);
    }

    private sealed record NamedDto([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("name")] string Name);
    private sealed record TxDto([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("month_id")] Guid MonthId);
    private sealed record MoneyDto([property: JsonPropertyName("crc")] decimal Crc, [property: JsonPropertyName("usd")] decimal Usd);
    private sealed record OtherDto([property: JsonPropertyName("category_name")] string CategoryName, [property: JsonPropertyName("actual")] MoneyDto Actual);
    private sealed record WeekDto([property: JsonPropertyName("week_number")] int WeekNumber, [property: JsonPropertyName("total")] MoneyDto Total);
    private sealed record SummaryDto(
        [property: JsonPropertyName("expenses_total")] MoneyDto ExpensesTotal,
        [property: JsonPropertyName("other_spending")] List<OtherDto> OtherSpending,
        [property: JsonPropertyName("weekly_budgeted")] List<WeekDto> WeeklyBudgeted);
    private sealed record MonthDto([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("year")] int Year, [property: JsonPropertyName("month_number")] int MonthNumber, [property: JsonPropertyName("week_count")] int WeekCount);
    private sealed record DashDto(
        [property: JsonPropertyName("month")] MonthDto Month,
        [property: JsonPropertyName("exchange_rate")] decimal? ExchangeRate,
        [property: JsonPropertyName("rate_source")] string? RateSource,
        [property: JsonPropertyName("rate_unavailable")] bool RateUnavailable,
        [property: JsonPropertyName("summary")] SummaryDto? Summary);
}
