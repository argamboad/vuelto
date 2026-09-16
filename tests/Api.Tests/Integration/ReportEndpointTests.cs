using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using UglyToad.PdfPig;
using Vuelto.Api.Features.Reports.Pdf;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests.Integration;

/// <summary>
/// REPORTS-1/2/7/8 over HTTP through the real app (RLS enforced, real local file storage): 401 anonymous; 400
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

    // ---- REPORTS-8: email me, and the account's language ----

    /// <summary>Stands in for the outbox sender so nothing leaves the test host.</summary>
    private sealed class CapturingEmailSender : IEmailSender
    {
        public readonly List<(string To, string Subject, IReadOnlyList<EmailAttachment> Attachments)> Sent = [];

        public Task SendAsync(string to, string subject, string htmlBody, IReadOnlyList<EmailInlineImage>? inlineImages = null,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        {
            EmailAttachment.Validate(attachments);
            lock (Sent) Sent.Add((to, subject, attachments ?? []));
            return Task.CompletedTask;
        }
    }

    /// <summary>A host whose app-facing email sender is <paramref name="capture"/>, and a signed-in client for it.</summary>
    private async Task<(HttpClient Client, SeededUser User)> EmailHostAsync(CapturingEmailSender capture)
    {
        var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IEmailSender>(capture)));
        var user = await _factory.SeedUserAsync(TenantRoles.Member);
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _factory.IssueAccessToken(user));
        return (client, user);
    }

    private static async Task<TxDto> AddSpendAsync(HttpClient client)
    {
        var category = (await client.GetFromJsonAsync<List<NamedDto>>("/api/categories"))![0];
        var bank = (await client.GetFromJsonAsync<List<NamedDto>>("/api/banks"))![0];
        var created = await client.PostAsJsonAsync("/api/transactions", new
        {
            payee = "Soda Tapia", bank_id = bank.Id, payment_method = "credit_card", original_amount = 15_750m, currency = "CRC",
            transaction_date = "2026-06-10", category_id = category.Id, transaction_type = "extraordinary", exchange_rate = 500m,
        });
        return (await created.Content.ReadFromJsonAsync<TxDto>())!;
    }

    [Fact]
    public async Task EmailMe_QueuesOneMailToTheCaller_WithThePdf_InTheAccountsLanguage()
    {
        var capture = new CapturingEmailSender();
        var (client, user) = await EmailHostAsync(capture);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/auth/locale", new { locale = "es" })).StatusCode);
        var tx = await AddSpendAsync(client);

        var res = await client.PostAsJsonAsync("/api/reports/pdf/email", new { month_id = tx.MonthId });

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var body = (await res.Content.ReadFromJsonAsync<EmailDto>())!;
        Assert.Equal((user.Email, "report-2026-05-28_2026-06-24.pdf"), (body.SentTo, body.FileName));
        var sent = Assert.Single(capture.Sent);
        Assert.Equal(user.Email, sent.To);
        Assert.StartsWith("Tu informe de gastos", sent.Subject);
        var file = Assert.Single(sent.Attachments);
        Assert.Equal("application/pdf", file.MediaType);
        using var pdf = PdfDocument.Open(file.Content);
        Assert.Contains("Informe de gastos", string.Join(" ", pdf.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text)))));

        // The download follows the same saved language when the request names none, and an explicit one wins.
        var download = (await (await client.PostAsJsonAsync("/api/reports/pdf", new { month_id = tx.MonthId })).Content.ReadFromJsonAsync<PdfDto>())!;
        var english = (await (await client.PostAsJsonAsync("/api/reports/pdf", new { month_id = tx.MonthId, language = "en" })).Content.ReadFromJsonAsync<PdfDto>())!;
        Assert.Contains("Informe de gastos", await PdfTextAsync(download.DownloadUrl));
        Assert.Contains("Spending report", await PdfTextAsync(english.DownloadUrl));
    }

    private async Task<string> PdfTextAsync(string downloadUrl)
    {
        var bytes = await _factory.CreateClient().GetByteArrayAsync(downloadUrl);
        using var pdf = PdfDocument.Open(bytes);
        return string.Join(" ", pdf.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
    }

    [Fact]
    public async Task EmailMe_IsCapped_PerPersonPerDay()
    {
        var capture = new CapturingEmailSender();
        var (client, _) = await EmailHostAsync(capture);
        var range = new { from = "2026-06-01", to = "2026-06-30", include_appendix = false };

        for (var i = 0; i < ReportEmailRateLimit.PermitLimit; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/reports/pdf/email", range)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/api/reports/pdf/email", range)).StatusCode);
        Assert.Equal(ReportEmailRateLimit.PermitLimit, capture.Sent.Count);

        // Someone else in the app still has their own allowance.
        var other = await _factory.SeedUserAsync(TenantRoles.Member);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _factory.IssueAccessToken(other));
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/reports/pdf/email", range)).StatusCode);
    }

    [Fact]
    public async Task EmailMe_GuardRails_SendNothing()
    {
        var capture = new CapturingEmailSender();
        var (client, _) = await EmailHostAsync(capture);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/api/reports/pdf/email", new { from = "2026-06-01", to = "2026-06-30" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/reports/pdf/email", new { month_id = Guid.CreateVersion7() })).StatusCode);
        var bad = await client.PostAsJsonAsync("/api/reports/pdf/email", new { from = "2026-06-01", to = "2026-06-30", display = "EUR" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("invalid_request", (await bad.Content.ReadFromJsonAsync<ErrorDto>())!.Error);
        Assert.Empty(capture.Sent);
    }

    private sealed record EmailDto([property: JsonPropertyName("sent_to")] string SentTo, [property: JsonPropertyName("file_name")] string FileName, [property: JsonPropertyName("period")] PeriodDto Period);
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
