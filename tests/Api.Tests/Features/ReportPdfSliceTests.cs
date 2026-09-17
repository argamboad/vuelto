using Microsoft.Extensions.Time.Testing;
using UglyToad.PdfPig;
using Vuelto.Api.Features.Reports;
using Vuelto.Api.Features.Reports.Pdf;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Features;

/// <summary>
/// REPORTS-7 on real Postgres, end to end through QuestPDF: the handler reads exactly what the Reports page reads
/// (analysis, trend, pending refunds, the CSV's rows), renders a real PDF — its text read back with PdfPig — and stores
/// it behind the CSV's signed link. Covers the option parsing, the household name, the appendix switch and its
/// parity with the export, the "no rate" month, a range, tenant isolation, and a payee Nunito cannot draw.
/// REPORTS-8: the language comes from the account's settings unless the request names one, and "Email me" queues one
/// email to the caller with the same PDF attached.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ReportPdfSliceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 15, 18, 0, 0, TimeSpan.Zero);

    private sealed class CapturingFileStorage : IFileStorage
    {
        public readonly Dictionary<string, (string ContentType, byte[] Bytes)> Stored = new();
        public TimeSpan? LastLifetime;

        public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, cancellationToken);
            Stored[key] = (contentType, ms.ToArray());
        }
        public Task<FileObject?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<FileObject?>(null);
        public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Stored.ContainsKey(key));
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) { Stored.Remove(key); return Task.CompletedTask; }
        public Task<Uri> GetDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken cancellationToken = default)
        {
            LastLifetime = lifetime;
            return Task.FromResult(new Uri("https://api.test/api/files/tok-1"));
        }
    }

    /// <summary>Records what would be sent, and applies the platform's attachment guard like the real sender.</summary>
    private sealed class CapturingEmailSender : IEmailSender
    {
        public readonly List<(string To, string Subject, string Html, IReadOnlyList<EmailAttachment> Attachments)> Sent = [];
        public bool Reject;

        public Task SendAsync(string to, string subject, string htmlBody, IReadOnlyList<EmailInlineImage>? inlineImages = null,
            IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken cancellationToken = default)
        {
            if (Reject) throw new ArgumentException("Attachments total more than the limit.", nameof(attachments));
            EmailAttachment.Validate(attachments);
            Sent.Add((to, subject, htmlBody, attachments ?? []));
            return Task.CompletedTask;
        }
    }

    private sealed class FixedRate(decimal? rate) : IExchangeRateResolver
    {
        public Task<ResolvedRate?> ResolveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(rate is { } r ? new ResolvedRate(r, RateSources.Cache, T0) : null);
    }

    private sealed record Ctx(AppDbContext Db, Guid Tenant, Guid UserId, ReportHandler Reports, ReportPdfHandler Pdf, CapturingFileStorage Files, CapturingEmailSender Email, Guid MonthId, Guid Groceries, Guid Bac);

    private async Task<Ctx> SeedAsync(decimal? rate = 500m, string household = "Casa Prueba", string? locale = null)
    {
        var tenant = Guid.CreateVersion7();
        var db = Fixture.CreateContext(tenant);

        db.Add(new Tenant { Id = tenant, Name = household, CreatedAt = T0, UpdatedAt = T0 });
        var user = new User { Email = $"ana-{tenant:N}@example.com", DisplayName = "Ana", EmailVerified = true, Locale = locale, CreatedAt = T0, UpdatedAt = T0 };
        db.Add(user);
        var groceries = new Category { TenantId = tenant, Name = "Groceries", CreatedAt = T0, UpdatedAt = T0 };
        var bac = new Bank { TenantId = tenant, Name = "BAC", CreatedAt = T0, UpdatedAt = T0 };
        var month = new Month
        {
            TenantId = tenant, Year = 2026, MonthNumber = 6, WeekCount = 4, Week1StartDate = new DateOnly(2026, 5, 28), CreatedAt = T0, UpdatedAt = T0,
        };
        db.AddRange(groceries, bac, month);
        db.Add(new MonthIncome { TenantId = tenant, MonthId = month.Id, Label = "Salary", Amount = 3_000m, Currency = "USD", CreatedAt = T0, UpdatedAt = T0 });
        db.AddRange(Enumerable.Range(0, 4).Select(i => new Week
        {
            TenantId = tenant, MonthId = month.Id, WeekNumber = i + 1,
            StartDate = new DateOnly(2026, 5, 28).AddDays(7 * i), EndDate = new DateOnly(2026, 6, 3).AddDays(7 * i),
        }));
        db.Add(new FixedExpense { TenantId = tenant, Name = "Supermarket", CategoryId = groceries.Id, BudgetCrc = 60_000m, PaymentMethod = "credit_card", CreatedAt = T0, UpdatedAt = T0 });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var files = new CapturingFileStorage();
        var clock = new FakeTimeProvider(T0);
        var reports = new ReportHandler(
            new EfRepository<Month>(db), new EfRepository<Week>(db), new EfRepository<Transaction>(db), new EfRepository<Category>(db),
            new EfRepository<Bank>(db), new EfRepository<Card>(db), new EfRepository<FixedExpense>(db), new EfRepository<VariableExpense>(db),
            new EfRepository<MonthIncome>(db), new TenantRepository(db), new TestCurrentTenant { TenantId = tenant }, files, new FixedRate(rate), clock);
        var email = new CapturingEmailSender();
        var pdf = new ReportPdfHandler(reports, new EfRepository<Month>(db), new EfRepository<Refund>(db),
            new TenantRepository(db), new UserRepository(db), new TestCurrentTenant { TenantId = tenant }, files, email, clock);
        return new Ctx(db, tenant, user.Id, reports, pdf, files, email, month.Id, groceries.Id, bac.Id);
    }

    private static async Task<Transaction> AddTxAsync(Ctx c, DateOnly date, decimal crc, string payee, string type = "budgeted", DateTimeOffset? created = null)
    {
        var tx = new Transaction
        {
            TenantId = c.Tenant, MonthId = c.MonthId, BankId = c.Bac, CategoryId = c.Groceries, Payee = payee, PaymentMethod = "credit_card",
            OriginalAmount = crc, Currency = "CRC", TransactionDate = date, AmountCrc = crc, AmountUsd = crc / 500m, ExchangeRateUsed = 500m,
            TransactionType = type, CreatedAt = created ?? T0, UpdatedAt = created ?? T0,
        };
        c.Db.Add(tx);
        await c.Db.SaveChangesAsync();
        c.Db.ChangeTracker.Clear();
        return tx;
    }

    private static ReportPdfOptions Options(string display = "both", bool appendix = true, string language = "en") =>
        new(display, "CRC", appendix, language, new DateOnly(2026, 6, 15));

    private static async Task<ReportPeriod> JuneAsync(Ctx c) => (await c.Reports.ResolvePeriodAsync(c.MonthId, null, null, default)).Period!;

    /// <summary>The PDF's words, in reading order, one string per page.</summary>
    private static List<string> PagesText(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return doc.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))).ToList();
    }

    // ---- options ----

    [Fact]
    public async Task ParseOptions_Defaults_AndTheTodayFromTheClock()
    {
        var c = await SeedAsync();
        var (options, error) = await c.Pdf.ParseOptionsAsync(new ReportPdfRequest(), c.UserId, default);
        Assert.Null(error);
        Assert.Equal(new ReportPdfOptions("both", "CRC", true, "en", new DateOnly(2026, 6, 15)), options);

        var (custom, _) = await c.Pdf.ParseOptionsAsync(new ReportPdfRequest(Display: "usd", ChartCurrency: "Usd", IncludeAppendix: false, Language: "ES", Today: new DateOnly(2026, 6, 1)), c.UserId, default);
        Assert.Equal(new ReportPdfOptions("USD", "USD", false, "es", new DateOnly(2026, 6, 1)), custom);
    }

    [Theory]
    [InlineData("es", null, "es")]  // the account's saved language
    [InlineData("ES", null, "es")]
    [InlineData("fr", null, "en")]  // a language the PDF does not ship: English
    [InlineData(null, null, "en")]  // never chosen: English
    [InlineData("es", "en", "en")]  // an explicit request wins (API callers)
    public async Task ParseOptions_Language_FollowsTheAccountSettings(string? locale, string? requested, string expected)
    {
        var c = await SeedAsync(locale: locale);
        var (options, _) = await c.Pdf.ParseOptionsAsync(new ReportPdfRequest(Language: requested), c.UserId, default);
        Assert.Equal(expected, options!.Language);
    }

    [Fact]
    public async Task ParseOptions_AppendixColumns_AreNormalized_AndAllMeansNull()
    {
        var c = await SeedAsync();

        var (some, error) = await c.Pdf.ParseOptionsAsync(new ReportPdfRequest(AppendixColumns: [" Notes", "AMOUNT", "date", "amount", "payee"]), c.UserId, default);
        Assert.Null(error);
        Assert.Equal(["amount", "notes"], some!.AppendixColumns); // print order, no repeats; date and payee are implied
        Assert.True(some.Shows("date") && some.Shows("payee") && some.Shows("notes"));
        Assert.False(some.Shows("bank"));

        var (none, _) = await c.Pdf.ParseOptionsAsync(new ReportPdfRequest(AppendixColumns: []), c.UserId, default);
        Assert.Empty(none!.AppendixColumns!); // date and payee only

        var (all, _) = await c.Pdf.ParseOptionsAsync(new ReportPdfRequest(AppendixColumns: [.. ReportPdfColumns.Optional]), c.UserId, default);
        Assert.Null(all!.AppendixColumns); // every column is the default appendix
    }

    [Fact]
    public async Task ParseOptions_AnUnknownColumn_IsRefused()
    {
        var c = await SeedAsync();
        var (options, error) = await c.Pdf.ParseOptionsAsync(new ReportPdfRequest(AppendixColumns: ["amount", "tip"]), c.UserId, default);
        Assert.Null(options);
        Assert.Equal("invalid_request", error!.Error);
        Assert.Contains("'tip'", error.Message);
    }

    [Theory]
    [InlineData("EUR", null, null)]
    [InlineData(null, "both", null)]
    [InlineData(null, "EUR", null)]
    [InlineData(null, null, "fr")]
    public async Task ParseOptions_RejectsUnknownValues(string? display, string? chart, string? language)
    {
        var c = await SeedAsync();
        var (options, error) = await c.Pdf.ParseOptionsAsync(new ReportPdfRequest(Display: display, ChartCurrency: chart, Language: language), c.UserId, default);
        Assert.Null(options);
        Assert.Equal("invalid_request", error!.Error);
    }

    // ---- the file ----

    [Fact]
    public async Task Create_StoresARealPdf_BehindTheSignedLink()
    {
        var c = await SeedAsync();
        await AddTxAsync(c, new DateOnly(2026, 6, 10), 5_000m, "Super MAS");

        var response = await c.Pdf.CreateAsync(await JuneAsync(c), Options(), default);

        Assert.Equal("report-2026-05-28_2026-06-24.pdf", response.FileName);
        Assert.Equal("https://api.test/api/files/tok-1", response.DownloadUrl);
        Assert.Equal((new DateOnly(2026, 5, 28), new DateOnly(2026, 6, 24)), (response.Period.From, response.Period.To));
        Assert.Equal(900, response.ExpiresInSeconds);
        Assert.Equal(TimeSpan.FromMinutes(15), c.Files.LastLifetime);

        var (key, stored) = Assert.Single(c.Files.Stored);
        Assert.StartsWith("exports/reports/", key);
        Assert.EndsWith("/report-2026-05-28_2026-06-24.pdf", key);
        Assert.Equal("application/pdf", stored.ContentType);
        Assert.Equal("%PDF-"u8.ToArray(), stored.Bytes[..5]);

        var pages = PagesText(stored.Bytes);
        Assert.True(pages.Count >= 2); // the report, then the landscape appendix
        Assert.Contains("Spending report", pages[0]);
        Assert.Contains("Casa Prueba", pages[0]);
        Assert.Contains("June 2026", pages[0]);
        Assert.Contains("₡5,000.00", pages[0]);   // the total-spend tile
        Assert.Contains("Page 1 of", pages[0]);
        var report = string.Join(" ", pages[..^1]);
        Assert.Contains("By category", report);
        Assert.Contains("Groceries", report);
        Assert.DoesNotContain("Super MAS", report); // payees only in the appendix
        Assert.Contains("Transactions", pages[^1]);
        Assert.Contains("Super MAS", pages[^1]);
    }

    [Fact]
    public async Task Render_InSpanish()
    {
        var c = await SeedAsync();
        await AddTxAsync(c, new DateOnly(2026, 6, 10), 5_000m, "Super MAS");

        var report = await c.Pdf.RenderAsync(await JuneAsync(c), Options(language: "es"), default);

        var text = string.Join(" ", PagesText(report.Content));
        Assert.Contains("Informe de gastos", text);
        Assert.Contains("Junio 2026", text);
        Assert.Contains("₡5.000,00", text);
        Assert.Contains("Transacciones", text);
    }

    [Fact]
    public async Task Appendix_IsExactlyTheCsvExportsRows()
    {
        var c = await SeedAsync();
        await AddTxAsync(c, new DateOnly(2026, 6, 1), 1_000m, "Older", created: T0);
        await AddTxAsync(c, new DateOnly(2026, 6, 10), 2_000m, "Same day, earlier", created: T0);
        await AddTxAsync(c, new DateOnly(2026, 6, 10), 3_000m, "Same day, later", created: T0.AddMinutes(5));
        await AddTxAsync(c, new DateOnly(2026, 6, 12), 4_000m, "Income", type: "inflow");
        await AddTxAsync(c, new DateOnly(2026, 6, 30), 9_000m, "Outside the window");
        var period = await JuneAsync(c);

        var export = await c.Reports.ExportRowsAsync(period, null, null, default);
        var report = await c.Pdf.RenderAsync(period, Options(), default);

        Assert.Equal(["Income", "Same day, later", "Same day, earlier", "Older"], export.Select(r => r.Payee));
        Assert.Equal(export.Select(r => r.Payee), report.Model.Appendix!.Rows.Select(r => r[1].Text));
    }

    [Fact]
    public async Task WithoutTheAppendix_ThereAreNoTransactionPages()
    {
        var c = await SeedAsync();
        await AddTxAsync(c, new DateOnly(2026, 6, 10), 5_000m, "Super MAS");

        var report = await c.Pdf.RenderAsync(await JuneAsync(c), Options(appendix: false), default);

        Assert.Null(report.Model.Appendix);
        var text = string.Join(" ", PagesText(report.Content));
        Assert.DoesNotContain("Super MAS", text);
        Assert.DoesNotContain("Transactions", text);
    }

    [Fact]
    public async Task PendingRefunds_AreTheUnplannedTilesRefundableFigure()
    {
        var c = await SeedAsync();
        var lunch = await AddTxAsync(c, new DateOnly(2026, 6, 10), 10_000m, "Clinic", type: "unplanned_essential");
        var other = await AddTxAsync(c, new DateOnly(2026, 6, 11), 4_000m, "Pharmacy", type: "unplanned_essential");
        c.Db.AddRange(
            new Refund { TenantId = c.Tenant, MonthId = c.MonthId, TransactionId = lunch.Id, Payee = "Clinic", TransactionDate = lunch.TransactionDate, Percentage = 50m, AmountCrc = 5_000m, AmountUsd = 10m, CreatedAt = T0, UpdatedAt = T0 },
            new Refund { TenantId = c.Tenant, MonthId = c.MonthId, TransactionId = other.Id, Payee = "Pharmacy", TransactionDate = other.TransactionDate, Percentage = 100m, AmountCrc = 4_000m, AmountUsd = 8m, Status = RefundStatuses.Received, CreatedAt = T0, UpdatedAt = T0 });
        await c.Db.SaveChangesAsync();
        c.Db.ChangeTracker.Clear();

        var report = await c.Pdf.RenderAsync(await JuneAsync(c), Options(), default);

        Assert.Equal("₡5,000.00 refundable", report.Model.Kpis[3].Sub); // the received one is already income
    }

    [Fact]
    public async Task NoRate_StillRenders_AndSaysSo()
    {
        var c = await SeedAsync(rate: null);
        await AddTxAsync(c, new DateOnly(2026, 6, 10), 5_000m, "Super MAS");

        var report = await c.Pdf.RenderAsync(await JuneAsync(c), Options(), default);

        Assert.Null(report.Model.Pace!.Caption);
        Assert.Contains("Today's exchange rate isn't available", string.Join(" ", PagesText(report.Content)));
    }

    [Fact]
    public async Task ARange_HasNoMonthPieces()
    {
        var c = await SeedAsync();
        await AddTxAsync(c, new DateOnly(2026, 6, 10), 5_000m, "Super MAS");
        var period = (await c.Reports.ResolvePeriodAsync(null, "2026-06-01", "2026-06-30", default)).Period!;

        var report = await c.Pdf.RenderAsync(period, Options(), default);

        Assert.Null(report.Model.Pace);
        Assert.Equal("1 Jun 2026 – 30 Jun 2026", report.Model.Header.Heading);
        Assert.Equal("report-2026-06-01_2026-06-30.pdf", report.FileName);
        Assert.Equal(["class", "bank", "method"], report.Model.Charts.Select(x => x.Key));
    }

    [Fact]
    public async Task AnotherHouseholdsRows_NeverAppear()
    {
        var mine = await SeedAsync(household: "Mine");
        var theirs = await SeedAsync(household: "Theirs");
        await AddTxAsync(mine, new DateOnly(2026, 6, 10), 5_000m, "My purchase");
        await AddTxAsync(theirs, new DateOnly(2026, 6, 10), 7_000m, "Their purchase");

        var report = await mine.Pdf.RenderAsync(await JuneAsync(mine), Options(), default);

        var text = string.Join(" ", PagesText(report.Content));
        Assert.Contains("Mine", text);
        Assert.Contains("My purchase", text);
        Assert.DoesNotContain("Their purchase", text);
        Assert.DoesNotContain("Theirs", text);
    }

    [Fact]
    public async Task APayeeTheFontCannotDraw_DoesNotBreakTheReport()
    {
        var c = await SeedAsync();
        await AddTxAsync(c, new DateOnly(2026, 6, 10), 5_000m, "Pizza 🍕 <b>&amp; 寿司");

        var report = await c.Pdf.RenderAsync(await JuneAsync(c), Options(), default);

        Assert.Contains("Pizza", string.Join(" ", PagesText(report.Content)));
    }

    // ---- REPORTS-8: email me ----

    [Fact]
    public async Task Email_QueuesOneMailToTheCaller_WithThePdfAttached_InTheAccountsLanguage()
    {
        var c = await SeedAsync(locale: "es");
        await AddTxAsync(c, new DateOnly(2026, 6, 10), 5_000m, "Super MAS");
        var (options, _) = await c.Pdf.ParseOptionsAsync(new ReportPdfRequest(), c.UserId, default);

        var result = await c.Pdf.EmailAsync(await JuneAsync(c), options!, c.UserId, default);

        var sent = Assert.Single(c.Email.Sent);
        Assert.Equal($"ana-{c.Tenant:N}@example.com", sent.To);
        Assert.Null(result.Error);
        Assert.Equal(sent.To, result.Response!.SentTo);
        Assert.Equal("report-2026-05-28_2026-06-24.pdf", result.Response.FileName);
        Assert.Equal("Tu informe de gastos: Junio 2026", sent.Subject);
        Assert.Contains("Casa Prueba", sent.Html);
        Assert.Contains("₡5.000,00", sent.Html);

        var file = Assert.Single(sent.Attachments);
        Assert.Equal(("report-2026-05-28_2026-06-24.pdf", "application/pdf"), (file.FileName, file.MediaType));
        Assert.Contains("Informe de gastos", string.Join(" ", PagesText(file.Content)));
        Assert.Empty(c.Files.Stored); // nothing kept in storage: the attachment is the copy
    }

    [Fact]
    public async Task Email_InEnglish_WhenTheAccountSaysSo()
    {
        var c = await SeedAsync(locale: "en");
        await AddTxAsync(c, new DateOnly(2026, 6, 10), 5_000m, "Super MAS");
        var (options, _) = await c.Pdf.ParseOptionsAsync(new ReportPdfRequest(), c.UserId, default);

        await c.Pdf.EmailAsync(await JuneAsync(c), options!, c.UserId, default);

        var sent = Assert.Single(c.Email.Sent);
        Assert.Equal("Your spending report: June 2026", sent.Subject);
        Assert.Contains("₡5,000.00", sent.Html);
    }

    [Fact]
    public async Task Email_ForAnUnknownCaller_SendsNothing()
    {
        var c = await SeedAsync();
        var (options, _) = await c.Pdf.ParseOptionsAsync(new ReportPdfRequest(), c.UserId, default);

        var result = await c.Pdf.EmailAsync(await JuneAsync(c), options!, Guid.CreateVersion7(), default);

        Assert.True(result.UnknownUser);
        Assert.Empty(c.Email.Sent);
    }

    [Fact]
    public async Task Email_WhenTheFileIsTooLargeToAttach_SaysSo()
    {
        var c = await SeedAsync();
        c.Email.Reject = true;
        var (options, _) = await c.Pdf.ParseOptionsAsync(new ReportPdfRequest(), c.UserId, default);

        var result = await c.Pdf.EmailAsync(await JuneAsync(c), options!, c.UserId, default);

        Assert.Equal("report_too_large", result.Error!.Error);
        Assert.Null(result.Response);
    }
}
