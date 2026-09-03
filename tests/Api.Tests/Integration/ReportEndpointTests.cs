using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests.Integration;

/// <summary>
/// REPORTS-1/2 over HTTP through the real app (RLS enforced, real local file storage): 401 anonymous; 400
/// period codes; the analysis for a member's month; the export returns a signed link that downloads the CSV
/// anonymously (the token IS the authorization, ADR-010); uniform 404.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class ReportEndpointTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Fact]
    public async Task Anonymous_IsRefused()
    {
        var anon = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/reports/category-analysis?from=2026-06-01&to=2026-06-30")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/reports/transactions/export?from=2026-06-01&to=2026-06-30", null)).StatusCode);
    }

    [Fact]
    public async Task Member_ReadsTheAnalysis_AndDownloadsTheExport()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);
        var category = (await client.GetFromJsonAsync<List<NamedDto>>("/api/categories"))![0];
        var bank = (await client.GetFromJsonAsync<List<NamedDto>>("/api/banks"))![0];

        var noPeriod = await client.GetAsync("/api/reports/category-analysis");
        Assert.Equal(HttpStatusCode.BadRequest, noPeriod.StatusCode);
        Assert.Equal("period_required", (await noPeriod.Content.ReadFromJsonAsync<ErrorDto>())!.Error);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/reports/category-analysis?month_id={Guid.CreateVersion7()}")).StatusCode);

        var created = await client.PostAsJsonAsync("/api/transactions", new
        {
            payee = "Café, \"El\" Punto", bank_id = bank.Id, payment_method = "credit_card", original_amount = 15_750m, currency = "CRC",
            transaction_date = "2026-06-10", category_id = category.Id, transaction_type = "extraordinary", exchange_rate = 500m,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var tx = (await created.Content.ReadFromJsonAsync<TxDto>())!;

        var report = (await client.GetFromJsonAsync<ReportDto>($"/api/reports/category-analysis?month_id={tx.MonthId}"))!;
        Assert.True(report.SingleMonth);
        Assert.Equal((new DateOnly(2026, 5, 28), new DateOnly(2026, 6, 24)), (report.Period.From, report.Period.To));
        var entry = Assert.Single(report.Extraordinary);
        Assert.Equal((category.Name, 15_750m, 31.5m), (entry.CategoryName, entry.TotalCrc, entry.TotalUsd));
        Assert.Empty(report.Budgeted);

        var export = await client.PostAsync($"/api/reports/transactions/export?month_id={tx.MonthId}", null);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var body = (await export.Content.ReadFromJsonAsync<ExportDto>())!;
        Assert.Equal(1, body.RowCount);
        Assert.StartsWith("transactions-", body.FileName);
        Assert.EndsWith(".csv", body.FileName);

        var download = await _factory.CreateClient().GetAsync(body.DownloadUrl); // anonymous: the signed token authorizes
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(body.FileName, download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var csv = await download.Content.ReadAsStringAsync();
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("date,payee,category,class,amount_crc,amount_usd,exchange_rate_used,payment_method,bank,source", lines[0]);
        Assert.Equal($"2026-06-10,\"Café, \"\"El\"\" Punto\",{category.Name},extraordinary,15750.00,31.50,500.0000,credit_card,{bank.Name},manual", lines[1]);
    }

    private sealed record NamedDto([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("name")] string Name);
    private sealed record TxDto([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("month_id")] Guid MonthId);
    private sealed record ErrorDto([property: JsonPropertyName("error")] string Error, [property: JsonPropertyName("message")] string Message);
    private sealed record PeriodDto([property: JsonPropertyName("from")] DateOnly From, [property: JsonPropertyName("to")] DateOnly To);
    private sealed record EntryDto([property: JsonPropertyName("category_name")] string CategoryName, [property: JsonPropertyName("total_crc")] decimal TotalCrc, [property: JsonPropertyName("total_usd")] decimal TotalUsd, [property: JsonPropertyName("budgeted_crc")] decimal? BudgetedCrc);
    private sealed record ReportDto([property: JsonPropertyName("period")] PeriodDto Period, [property: JsonPropertyName("single_month")] bool SingleMonth, [property: JsonPropertyName("budgeted")] List<EntryDto> Budgeted, [property: JsonPropertyName("extraordinary")] List<EntryDto> Extraordinary);
    private sealed record ExportDto([property: JsonPropertyName("download_url")] string DownloadUrl, [property: JsonPropertyName("file_name")] string FileName, [property: JsonPropertyName("row_count")] int RowCount);
}
