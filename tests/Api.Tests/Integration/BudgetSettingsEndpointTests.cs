using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests.Integration;

/// <summary>
/// BUDGET-1 over HTTP through the real app (RLS enforced): the group's auth policy refuses anonymous
/// callers, a plain member can read and save, and validation surfaces as 400 with the shared error shape.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class BudgetSettingsEndpointTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public async Task Anonymous_IsRefused_OnReadAndWrite()
    {
        var anon = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/budget-settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anon.PutAsJsonAsync("/api/budget-settings", ValidBody())).StatusCode);
    }

    [Fact]
    public async Task Member_CanSaveAndReadBack()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);

        var before = await client.GetFromJsonAsync<SettingsDto>("/api/budget-settings");
        Assert.True(before!.IsDefault);

        var put = await client.PutAsJsonAsync("/api/budget-settings", ValidBody());
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var after = await client.GetFromJsonAsync<SettingsDto>("/api/budget-settings");
        Assert.False(after!.IsDefault);
        Assert.Equal(1, after.WeekStartWeekday);
        Assert.Equal("first_of_month", after.MonthAnchor);
        Assert.Equal(1500.00m, after.PrimaryIncome4w);
        Assert.Equal("CRC", after.SecondaryIncomeCurrency);
    }

    [Fact]
    public async Task InvalidBody_Is400_WithTheSharedErrorShape()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);

        var res = await client.PutAsJsonAsync("/api/budget-settings", ValidBody() with { week_start_weekday = 9 });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var error = await res.Content.ReadFromJsonAsync<ErrorDto>();
        Assert.Equal("invalid_request", error!.Error);
        Assert.Contains("week_start_weekday", error.Message);
    }

    private static Body ValidBody() => new(1, "first_of_month", 1500.00m, 1800.00m, "USD", 400000m, 500000m, "CRC");

    // Wire-shaped on purpose (snake_case) — this test is the contract the Postman collection documents.
    private sealed record Body(
        int week_start_weekday, string month_anchor,
        decimal primary_income_4w, decimal primary_income_5w, string primary_income_currency,
        decimal secondary_income_4w, decimal secondary_income_5w, string secondary_income_currency);

    private sealed record SettingsDto(
        [property: JsonPropertyName("week_start_weekday")] int WeekStartWeekday,
        [property: JsonPropertyName("month_anchor")] string MonthAnchor,
        [property: JsonPropertyName("primary_income_4w")] decimal PrimaryIncome4w,
        [property: JsonPropertyName("secondary_income_currency")] string SecondaryIncomeCurrency,
        [property: JsonPropertyName("is_default")] bool IsDefault);

    private sealed record ErrorDto(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("message")] string Message);
}
