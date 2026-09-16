using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using UglyToad.PdfPig;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests.Integration;

/// <summary>
/// REPORTS-1/2/7 over HTTP through the real app (RLS enforced, real local file storage): 401 anonymous; 400
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
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/reports/pdf", new { from = "2026-06-01", to = "2026-06-30" })).StatusCode);
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
        Assert.Equal(bank.Name, Assert.Single(report.ByBank).Label);

        var trend = (await client.GetFromJsonAsync<TrendDto>("/api/reports/months-trend?count=6"))!;
        Assert.Equal(tx.MonthId, Assert.Single(trend.Months).MonthId);
        Assert.Equal(15_750m, trend.Months[0].Spend.Crc);
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
        Assert.Equal("date,payee,category,class,amount_crc,amount_usd,exchange_rate_used,payment_method,bank,source,card,notes", lines[0]);
        Assert.Equal($"2026-06-10,\"Café, \"\"El\"\" Punto\",{category.Name},extraordinary,15750.00,31.50,500.0000,credit_card,{bank.Name},manual,,", lines[1]); // trailing card + notes columns, both empty here
    }

    [Fact]
    public async Task Member_DownloadsTheReportAsAPdf()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);
        var category = (await client.GetFromJsonAsync<List<NamedDto>>("/api/categories"))![0];
        var bank = (await client.GetFromJsonAsync<List<NamedDto>>("/api/banks"))![0];
        var created = await client.PostAsJsonAsync("/api/transactions", new
        {
            payee = "Soda Tapia", bank_id = bank.Id, payment_method = "credit_card", original_amount = 15_750m, currency = "CRC",
            transaction_date = "2026-06-10", category_id = category.Id, transaction_type = "extraordinary", exchange_rate = 500m,
        });
        var tx = (await created.Content.ReadFromJsonAsync<TxDto>())!;

        var res = await client.PostAsJsonAsync("/api/reports/pdf", new { month_id = tx.MonthId, display = "CRC", language = "es", today = "2026-06-12" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = (await res.Content.ReadFromJsonAsync<PdfDto>())!;
        Assert.Equal("report-2026-05-28_2026-06-24.pdf", body.FileName);
        Assert.Equal((new DateOnly(2026, 5, 28), new DateOnly(2026, 6, 24)), (body.Period.From, body.Period.To));
        Assert.Equal(900, body.ExpiresInSeconds);

        var download = await _factory.CreateClient().GetAsync(body.DownloadUrl); // anonymous: the signed token authorizes
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(body.FileName, download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        using var pdf = PdfDocument.Open(await download.Content.ReadAsByteArrayAsync());
        var text = string.Join(" ", pdf.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
        Assert.Contains("Informe de gastos", text);
        Assert.Contains("₡15.750,00", text);
        Assert.Contains("Soda Tapia", text);
        Assert.DoesNotContain("$31,50", text); // "show in" ₡ only
    }

    [Fact]
    public async Task Pdf_GuardRails()
    {
        var member = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = _factory.CreateClientFor(member);

        async Task<(HttpStatusCode Status, string? Code)> Post(object body)
        {
            var res = await client.PostAsJsonAsync("/api/reports/pdf", body);
            return (res.StatusCode, res.StatusCode == HttpStatusCode.OK ? null : (await res.Content.ReadFromJsonAsync<ErrorDto>())?.Error);
        }

        Assert.Equal((HttpStatusCode.BadRequest, "period_required"), await Post(new { }));
        var noBody = await client.PostAsync("/api/reports/pdf", null); // an empty body is allowed and means "no period"
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
        Assert.Equal("period_required", (await noBody.Content.ReadFromJsonAsync<ErrorDto>())!.Error);
        Assert.Equal((HttpStatusCode.BadRequest, "period_ambiguous"), await Post(new { month_id = Guid.CreateVersion7(), from = "2026-06-01", to = "2026-06-30" }));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_request"), await Post(new { from = "2026-06-01", to = "2026-06-30", display = "EUR" }));
        Assert.Equal((HttpStatusCode.BadRequest, "invalid_request"), await Post(new { from = "2026-06-01", to = "2026-06-30", language = "fr" }));
        Assert.Equal(HttpStatusCode.NotFound, (await Post(new { month_id = Guid.CreateVersion7() })).Status);

        // Another household's month is the same uniform 404.
        var other = _factory.CreateClientFor(await _factory.SeedUserAsync(TenantRoles.Owner));
        var category = (await other.GetFromJsonAsync<List<NamedDto>>("/api/categories"))![0];
        var bank = (await other.GetFromJsonAsync<List<NamedDto>>("/api/banks"))![0];
        var theirs = (await (await other.PostAsJsonAsync("/api/transactions", new
        {
            payee = "Theirs", bank_id = bank.Id, payment_method = "credit_card", original_amount = 1_000m, currency = "CRC",
            transaction_date = "2026-06-10", category_id = category.Id, transaction_type = "budgeted", exchange_rate = 500m,
        })).Content.ReadFromJsonAsync<TxDto>())!;
        Assert.Equal(HttpStatusCode.NotFound, (await Post(new { month_id = theirs.MonthId })).Status);

        // A range renders.
        Assert.Equal(HttpStatusCode.OK, (await Post(new { from = "2026-06-01", to = "2026-06-30", include_appendix = false })).Status);
    }

    private sealed record PdfDto([property: JsonPropertyName("download_url")] string DownloadUrl, [property: JsonPropertyName("file_name")] string FileName, [property: JsonPropertyName("period")] PeriodDto Period, [property: JsonPropertyName("expires_in_seconds")] int ExpiresInSeconds);
    private sealed record NamedDto([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("name")] string Name);
    private sealed record TxDto([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("month_id")] Guid MonthId);
    private sealed record ErrorDto([property: JsonPropertyName("error")] string Error, [property: JsonPropertyName("message")] string Message);
    private sealed record PeriodDto([property: JsonPropertyName("from")] DateOnly From, [property: JsonPropertyName("to")] DateOnly To);
    private sealed record EntryDto([property: JsonPropertyName("category_name")] string CategoryName, [property: JsonPropertyName("total_crc")] decimal TotalCrc, [property: JsonPropertyName("total_usd")] decimal TotalUsd, [property: JsonPropertyName("budgeted_crc")] decimal? BudgetedCrc);
    private sealed record GroupDto([property: JsonPropertyName("key")] string Key, [property: JsonPropertyName("label")] string Label, [property: JsonPropertyName("total_crc")] decimal TotalCrc);
    private sealed record ReportDto([property: JsonPropertyName("period")] PeriodDto Period, [property: JsonPropertyName("single_month")] bool SingleMonth, [property: JsonPropertyName("budgeted")] List<EntryDto> Budgeted, [property: JsonPropertyName("extraordinary")] List<EntryDto> Extraordinary, [property: JsonPropertyName("by_bank")] List<GroupDto> ByBank);
    private sealed record MoneyDto([property: JsonPropertyName("crc")] decimal Crc, [property: JsonPropertyName("usd")] decimal Usd);
    private sealed record TrendMonthDto([property: JsonPropertyName("month_id")] Guid MonthId, [property: JsonPropertyName("month_number")] int MonthNumber, [property: JsonPropertyName("income")] MoneyDto? Income, [property: JsonPropertyName("spend")] MoneyDto Spend);
    private sealed record TrendDto([property: JsonPropertyName("months")] List<TrendMonthDto> Months, [property: JsonPropertyName("rate_available")] bool RateAvailable);
    private sealed record ExportDto([property: JsonPropertyName("download_url")] string DownloadUrl, [property: JsonPropertyName("file_name")] string FileName, [property: JsonPropertyName("row_count")] int RowCount);
}
