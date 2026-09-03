using System.Net;
using Bunit;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>REPORTS-1/2 UI: newest month loads with budget columns and tone; range mode validates and loads; export POSTs the shown period and hands the absolute link to the launcher; the month page exports too.</summary>
public class ReportsPageTests : ComponentTestBase
{
    private const string M1 = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string M2 = "aaaaaaaa-0000-0000-0000-000000000002";
    private const string Months = $$"""[{"id":"{{M2}}","year":2026,"month_number":7},{"id":"{{M1}}","year":2026,"month_number":6}]""";
    private const string SingleMonth = """
        {"period":{"from":"2026-06-25","to":"2026-07-29"},"single_month":true,
         "budgeted":[{"category_id":"bbbbbbbb-0000-0000-0000-000000000001","category_name":"Groceries","total_crc":8000,"total_usd":16,"budgeted_crc":60000,"budgeted_usd":120},
                     {"category_id":"bbbbbbbb-0000-0000-0000-000000000002","category_name":"Housing","total_crc":70000,"total_usd":140,"budgeted_crc":60000,"budgeted_usd":120},
                     {"category_id":"bbbbbbbb-0000-0000-0000-000000000003","category_name":"Other","total_crc":100,"total_usd":0.2,"budgeted_crc":null,"budgeted_usd":null}],
         "extraordinary":[{"category_id":"bbbbbbbb-0000-0000-0000-000000000004","category_name":"Dining","total_crc":2000,"total_usd":4,"budgeted_crc":null,"budgeted_usd":null}],
         "unplanned_essential":[]}
        """;
    private const string Range = """
        {"period":{"from":"2026-01-01","to":"2026-06-30"},"single_month":false,
         "budgeted":[{"category_id":"bbbbbbbb-0000-0000-0000-000000000001","category_name":"Groceries","total_crc":8000,"total_usd":16,"budgeted_crc":null,"budgeted_usd":null}],
         "extraordinary":[],"unplanned_essential":[]}
        """;
    private const string Export = """{"download_url":"/api/files/tok-1","file_name":"transactions-2026-09-03.csv","row_count":4,"period":{"from":"2026-06-25","to":"2026-07-29"},"expires_in_seconds":900}""";

    [Fact]
    public async Task Loads_TheNewestMonth_WithBudgetColumns_AndTone()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, "/api/reports/category-analysis", SingleMonth);

        var cut = Render<Reports>();

        cut.WaitForElement("[data-testid='rep-budgeted']");
        Assert.Contains($"month_id={M2}", Assert.Single(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/reports/category-analysis").RequestUri!.Query);
        Assert.Contains("Reports_SingleMonthNote", cut.Find("[data-testid='rep-period']").TextContent);

        var rows = cut.FindAll("[data-testid='rep-budgeted'] [data-testid='rep-row']");
        Assert.Equal(3, rows.Count);
        var actuals = cut.FindAll("[data-testid='rep-budgeted'] [data-testid='rep-actual']");
        Assert.Contains("text-success", actuals[0].ClassName); // Groceries under budget
        Assert.Contains("text-danger", actuals[1].ClassName);  // Housing over
        Assert.DoesNotContain("text-success", actuals[2].ClassName); // no line → no tone
        Assert.DoesNotContain("text-danger", actuals[2].ClassName);
        Assert.Contains("—", cut.FindAll("[data-testid='rep-budget']")[2].TextContent);
        Assert.Contains("₡120,000.00", cut.Find("[data-testid='rep-budgeted'] [data-testid='rep-total']").TextContent); // budget total
        Assert.Contains("Dining", cut.Find("[data-testid='rep-extraordinary']").TextContent);
        Assert.Contains("Reports_NoneInClass", cut.Find("[data-testid='rep-unplanned']").TextContent);
        Assert.DoesNotContain(cut.FindAll("[data-testid='rep-extraordinary'] th"), th => th.TextContent.Contains("Reports_BudgetedCol")); // budget column only on the budgeted class
    }

    [Fact]
    public async Task RangeMode_ValidatesDates_ThenLoadsWithoutBudgetColumns()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, "/api/reports/category-analysis", Range);

        var cut = Render<Reports>();
        cut.WaitForElement("[data-testid='rep-mode']").Change("range");
        cut.Find("[data-testid='rep-load']").Click();
        Assert.Contains("Reports_DateRequired", cut.Find("[data-testid='rep-notice']").TextContent);

        cut.Find("[data-testid='rep-from']").Change("2026-06-30");
        cut.Find("[data-testid='rep-to']").Change("2026-01-01");
        cut.Find("[data-testid='rep-load']").Click();
        Assert.Contains("Reports_DateOrder", cut.Find("[data-testid='rep-notice']").TextContent);

        cut.Find("[data-testid='rep-from']").Change("2026-01-01");
        cut.Find("[data-testid='rep-to']").Change("2026-06-30");
        cut.Find("[data-testid='rep-load']").Click();

        cut.WaitForAssertion(() => Assert.Contains("Reports_MultiMonthNote", cut.Find("[data-testid='rep-period']").TextContent));
        Assert.Contains("from=2026-01-01&to=2026-06-30", Http.Requests.Last(r => r.RequestUri!.AbsolutePath == "/api/reports/category-analysis").RequestUri!.Query);
        Assert.Empty(cut.FindAll("[data-testid='rep-budget']"));
    }

    [Fact]
    public async Task Export_PostsTheShownPeriod_AndLaunchesTheAbsoluteLink()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, "/api/reports/category-analysis", SingleMonth);
        Http.On(HttpMethod.Post, "/api/reports/transactions/export", Export);

        var cut = Render<Reports>();
        cut.WaitForElement("[data-testid='rep-budgeted']");
        cut.Find("[data-testid='rep-export']").Click();

        cut.WaitForElement("[data-testid='rep-notice']");
        var post = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/reports/transactions/export");
        Assert.Contains($"month_id={M2}", post.RequestUri!.Query);
        var launched = Assert.Single(Downloads.Launched);
        Assert.Equal(("http://localhost/api/files/tok-1", "transactions-2026-09-03.csv"), launched);
        Assert.Contains("Reports_ExportReady[4]", cut.Find("[data-testid='rep-notice']").TextContent);
    }

    [Fact]
    public async Task NoMonths_DisablesExport_AndShowsTheEmptyHint()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", "[]");

        var cut = Render<Reports>();

        cut.WaitForElement("[data-testid='rep-no-months']");
        Assert.True(cut.Find("[data-testid='rep-export']").HasAttribute("disabled"));
        Assert.DoesNotContain(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/reports/category-analysis");
    }

    [Fact]
    public async Task MonthPage_ExportButton_PostsThatMonth_AndLaunches()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, $"/api/months/{M1}", $$"""{"id":"{{M1}}","year":2026,"month_number":6,"week_count":4,"week1_start_date":"2026-05-28","primary_income_amount":0,"primary_income_currency":"USD","secondary_income_amount":0,"secondary_income_currency":"USD","weeks":[{"week_number":1,"start_date":"2026-05-28","end_date":"2026-06-03"}]}""");
        Http.On(HttpMethod.Get, $"/api/months/{M1}/transactions", "[]");
        Http.On(HttpMethod.Get, $"/api/months/{M1}/refunds", "[]");
        Http.On(HttpMethod.Post, "/api/reports/transactions/export", Export);

        var cut = Render<MonthDetail>(p => p.Add(x => x.Id, Guid.Parse(M1)));
        cut.WaitForElement("[data-testid='month-export']").Click();

        cut.WaitForElement("[data-testid='month-notice']");
        Assert.Contains($"month_id={M1}", Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/reports/transactions/export").RequestUri!.Query);
        Assert.Equal("http://localhost/api/files/tok-1", Assert.Single(Downloads.Launched).Url);
    }
}
