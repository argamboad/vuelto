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
                     {"category_id":"bbbbbbbb-0000-0000-0000-000000000003","category_name":"Other","total_crc":100,"total_usd":0.2,"budgeted_crc":null,"budgeted_usd":null},
                     {"category_id":"bbbbbbbb-0000-0000-0000-000000000005","category_name":"Streaming - Disney","total_crc":8604.87,"total_usd":18.99,"budgeted_crc":0,"budgeted_usd":18.99},
                     {"category_id":"bbbbbbbb-0000-0000-0000-000000000006","category_name":"Streaming - Extra","total_crc":11000,"total_usd":25,"budgeted_crc":0,"budgeted_usd":18.99}],
         "extraordinary":[{"category_id":"bbbbbbbb-0000-0000-0000-000000000004","category_name":"Dining","total_crc":2000,"total_usd":4,"budgeted_crc":null,"budgeted_usd":null}],
         "unplanned_essential":[],
         "income":{"crc":200000,"usd":400},"budget_total":{"crc":150000,"usd":300},
         "by_bank":[{"key":"cccccccc-0000-0000-0000-000000000001","label":"BAC","total_crc":90000,"total_usd":180},{"key":"cccccccc-0000-0000-0000-000000000002","label":"","total_crc":9704.87,"total_usd":24.19}],
         "by_method":[{"key":"credit_card","label":"credit_card","total_crc":80000,"total_usd":160},{"key":"bank_account","label":"bank_account","total_crc":19704.87,"total_usd":44.19}],
         "spend_by_day":[{"date":"2026-06-26","total_crc":8000,"total_usd":16},{"date":"2026-07-03","total_crc":70000,"total_usd":140},{"date":"2026-07-20","total_crc":21704.87,"total_usd":48.19}]}
        """;
    private const string Trend = """
        {"months":[{"month_id":"aaaaaaaa-0000-0000-0000-000000000000","year":2026,"month_number":5,"income":{"crc":200000,"usd":400},"spend":{"crc":250000,"usd":500}},
                   {"month_id":"aaaaaaaa-0000-0000-0000-000000000001","year":2026,"month_number":6,"income":{"crc":200000,"usd":400},"spend":{"crc":120000,"usd":240}},
                   {"month_id":"aaaaaaaa-0000-0000-0000-000000000002","year":2026,"month_number":7,"income":{"crc":200000,"usd":400},"spend":{"crc":99704.87,"usd":204.19}}],
         "rate_available":true}
        """;
    private const string TrendNoRate = """{"months":[{"month_id":"aaaaaaaa-0000-0000-0000-000000000002","year":2026,"month_number":7,"income":null,"spend":{"crc":99704.87,"usd":204.19}}],"rate_available":false}""";
    /// <summary>Same spend (₡99,704.87 · $204.19) and plan (₡150,000), income below both.</summary>
    private static readonly string Overspent = SingleMonth.Replace("\"income\":{\"crc\":200000,\"usd\":400}", "\"income\":{\"crc\":50000,\"usd\":100}");
    /// <summary>Same month, but the rate chain came up empty — the API sends neither income nor budget total.</summary>
    private static readonly string NoRate = SingleMonth.Replace("\"income\":{\"crc\":200000,\"usd\":400},\"budget_total\":{\"crc\":150000,\"usd\":300}", "\"income\":null,\"budget_total\":null");
    private const string Range = """
        {"period":{"from":"2026-01-01","to":"2026-06-30"},"single_month":false,
         "budgeted":[{"category_id":"bbbbbbbb-0000-0000-0000-000000000001","category_name":"Groceries","total_crc":8000,"total_usd":16,"budgeted_crc":null,"budgeted_usd":null}],
         "extraordinary":[],"unplanned_essential":[],"income":null,"budget_total":null,
         "by_bank":[{"key":"cccccccc-0000-0000-0000-000000000001","label":"BAC","total_crc":8000,"total_usd":16}],
         "by_method":[{"key":"credit_card","label":"credit_card","total_crc":8000,"total_usd":16}],
         "spend_by_day":null}
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
        Assert.Equal(5, rows.Count);
        var actuals = cut.FindAll("[data-testid='rep-budgeted'] [data-testid='rep-actual']");
        Assert.Contains("text-success", actuals[0].ClassName); // Groceries under budget
        Assert.Contains("text-danger", actuals[1].ClassName);  // Housing over
        Assert.DoesNotContain("text-success", actuals[2].ClassName); // no line → no tone
        Assert.DoesNotContain("text-danger", actuals[2].ClassName);
        Assert.Contains("text-success", actuals[3].ClassName); // a $18.99 line judged in dollars: $18.99 spent is not over (its ₡ side vs ₡0 used to paint it red)
        Assert.Contains("text-danger", actuals[4].ClassName);  // $25 spent against $18.99 IS over
        Assert.Contains("—", cut.FindAll("[data-testid='rep-budget']")[2].TextContent);
        Assert.Contains("₡120,000.00", cut.Find("[data-testid='rep-budgeted'] [data-testid='rep-total']").TextContent); // budget total
        Assert.Contains("Dining", cut.Find("[data-testid='rep-extraordinary']").TextContent);
        Assert.Contains("Reports_NoneInClass", cut.Find("[data-testid='rep-unplanned']").TextContent);
        Assert.DoesNotContain(cut.FindAll("[data-testid='rep-extraordinary'] th"), th => th.TextContent.Contains("Reports_BudgetedCol")); // budget column only on the budgeted class
    }

    [Fact]
    public async Task ChartView_DrawsTheSameRows_WithBudgetOverlays_TheClassDonut_AndACurrencySwitch()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, "/api/reports/category-analysis", SingleMonth);

        var cut = Render<Reports>();
        cut.WaitForElement("[data-testid='rep-budgeted'] [data-testid='rep-row']"); // table by default (no stored preference)
        Assert.Empty(cut.FindAll("[data-testid='rep-donut']"));

        cut.Find("[data-testid='rep-view-chart']").Click();

        // Same five budgeted rows as bars, sorted by size; only ₡-budgeted lines get a ₡ budget overlay.
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("[data-testid='rep-budgeted-chart'] [data-testid='chart-bar']").Count));
        Assert.Empty(cut.FindAll("[data-testid='rep-budgeted'] [data-testid='rep-row']")); // the table is replaced, not duplicated
        var bars = cut.FindAll("[data-testid='rep-budgeted-chart'] [data-testid='chart-bar']");
        Assert.Contains("Housing", bars[0].TextContent); // largest first (70,000)
        Assert.Equal("true", bars[0].GetAttribute("data-over"));
        Assert.Equal(2, cut.FindAll("[data-testid='rep-budgeted-chart'] [data-testid='chart-budget']").Count); // Groceries + Housing have ₡ budgets; the $ lines and "Other" do not
        Assert.Contains("₡", cut.Find("[data-testid='rep-budgeted-chart-total']").TextContent);
        Assert.Equal(3, cut.FindAll("[data-testid='rep-donut'] [data-testid='chart-legend-item']").Count);
        Assert.Equal(2, cut.FindAll("[data-testid='rep-donut'] [data-testid='chart-slice']").Count); // budgeted + extraordinary; unplanned is empty
        Assert.Contains("Reports_NoneInClass", cut.Find("[data-testid='rep-unplanned']").TextContent);
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "appUi.setPref"); // remembered per device

        // Dollars: values re-label, and now only the $-budgeted lines carry an overlay.
        cut.Find("[data-testid='rep-cur-usd']").Click();
        cut.WaitForAssertion(() => Assert.Contains("$", cut.Find("[data-testid='rep-budgeted-chart-total']").TextContent));
        Assert.Equal(4, cut.FindAll("[data-testid='rep-budgeted-chart'] [data-testid='chart-budget']").Count); // the two Streaming lines + Groceries/Housing, whose fixture carries a $ side too
        Assert.Contains("$25.00", cut.FindAll("[data-testid='rep-budgeted-chart'] [data-testid='chart-value']").Select(v => v.TextContent).First(t => t.Contains("25")));

        cut.Find("[data-testid='rep-view-table']").Click();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("[data-testid='rep-budgeted'] [data-testid='rep-row']").Count)); // and back
    }

    [Fact]
    public async Task ChartView_IncomeDonut_MeasuresTheSpendAgainstTheMonthsIncome()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, "/api/reports/category-analysis", SingleMonth);

        var cut = Render<Reports>();
        cut.WaitForElement("[data-testid='rep-budgeted']");
        cut.Find("[data-testid='rep-view-chart']").Click();

        // Three class slices + Remaining; the hole says the income, not the sum; remaining = 200,000 − 99,704.87.
        cut.WaitForElement("[data-testid='rep-income-donut']");
        var legend = cut.FindAll("[data-testid='rep-income-donut'] [data-testid='chart-legend-item']");
        Assert.Equal(4, legend.Count);
        Assert.Contains("Reports_Remaining", legend[3].TextContent);
        Assert.Contains("₡100,295", legend[3].TextContent);
        Assert.Equal("₡200,000", cut.Find("[data-testid='rep-income-donut'] [data-testid='chart-center']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='rep-income-over']"));
        Assert.Equal(3, cut.FindAll("[data-testid='rep-donut'] [data-testid='chart-legend-item']").Count); // the class donut is unchanged beside it

        // Income vs budget: the active lines commit ₡150,000 of the ₡200,000; ₡50,000 is uncommitted.
        var budgetLegend = cut.FindAll("[data-testid='rep-budget-donut'] [data-testid='chart-legend-item']");
        Assert.Equal(2, budgetLegend.Count);
        Assert.Contains("Reports_BudgetLines", budgetLegend[0].TextContent);
        Assert.Contains("₡150,000", budgetLegend[0].TextContent);
        Assert.Contains("Reports_Uncommitted", budgetLegend[1].TextContent);
        Assert.Contains("₡50,000", budgetLegend[1].TextContent);
        Assert.Equal("₡200,000", cut.Find("[data-testid='rep-budget-donut'] [data-testid='chart-center']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='rep-budget-over']"));

        // Dollars: the same pictures in the other currency ($400 income, $204.19 spent, $300 planned).
        cut.Find("[data-testid='rep-cur-usd']").Click();
        cut.WaitForAssertion(() => Assert.Equal("$400", cut.Find("[data-testid='rep-income-donut'] [data-testid='chart-center']").TextContent));
        Assert.Contains("$196", cut.FindAll("[data-testid='rep-income-donut'] [data-testid='chart-legend-item']")[3].TextContent); // 400 − 204.19 = 195.81, whole dollars in the legend
        Assert.Contains("$100", cut.FindAll("[data-testid='rep-budget-donut'] [data-testid='chart-legend-item']")[1].TextContent);
    }

    [Fact]
    public async Task ChartView_IncomeDonut_Overspent_HasNoRemainingSlice_AndSaysByHowMuch()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, "/api/reports/category-analysis", Overspent);

        var cut = Render<Reports>();
        cut.WaitForElement("[data-testid='rep-budgeted']");
        cut.Find("[data-testid='rep-view-chart']").Click();

        cut.WaitForElement("[data-testid='rep-income-donut']");
        Assert.Equal(2, cut.FindAll("[data-testid='rep-income-donut'] [data-testid='chart-slice']").Count); // budgeted + discretionary; unplanned and remaining are zero
        Assert.Contains("₡0", cut.FindAll("[data-testid='rep-income-donut'] [data-testid='chart-legend-item']")[3].TextContent);
        Assert.Equal("₡50,000", cut.Find("[data-testid='rep-income-donut'] [data-testid='chart-center']").TextContent);
        Assert.Contains("Reports_OverBy[₡49,704.87]", cut.Find("[data-testid='rep-income-over']").TextContent);

        // The plan (₡150,000) also exceeds the income: a full ring of budget lines, no uncommitted slice, and the red line says by how much.
        Assert.Single(cut.FindAll("[data-testid='rep-budget-donut'] [data-testid='chart-slice']"));
        Assert.Contains("₡0", cut.FindAll("[data-testid='rep-budget-donut'] [data-testid='chart-legend-item']")[1].TextContent);
        Assert.Contains("Reports_BudgetOverBy[₡100,000.00]", cut.Find("[data-testid='rep-budget-over']").TextContent);
    }

    [Fact]
    public async Task ChartView_IncomeDonut_NoRate_SaysSo_AndIsAbsentForARange()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, "/api/reports/category-analysis", NoRate);

        var cut = Render<Reports>();
        cut.WaitForElement("[data-testid='rep-budgeted']");
        cut.Find("[data-testid='rep-view-chart']").Click();

        cut.WaitForElement("[data-testid='rep-income-card']");
        Assert.Contains("Reports_IncomeNoRate", cut.Find("[data-testid='rep-income-norate']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='rep-income-donut']"));
        Assert.Contains("Reports_IncomeNoRate", cut.Find("[data-testid='rep-budget-norate']").TextContent); // the budget card needs the same rate
        Assert.Empty(cut.FindAll("[data-testid='rep-budget-donut']"));
        Assert.NotEmpty(cut.FindAll("[data-testid='rep-donut']")); // the spend donut never depends on the rate

        // A date range has no month income: no card at all, and the class donut takes the full width again.
        Http.On(HttpMethod.Get, "/api/reports/category-analysis", Range);
        cut.Find("[data-testid='rep-mode']").Change("range");
        cut.Find("[data-testid='rep-from']").Change("2026-01-01");
        cut.Find("[data-testid='rep-to']").Change("2026-06-30");
        cut.Find("[data-testid='rep-load']").Click();
        cut.WaitForAssertion(() => Assert.Contains("Reports_MultiMonthNote", cut.Find("[data-testid='rep-period']").TextContent));
        Assert.Empty(cut.FindAll("[data-testid='rep-income-card']"));
        Assert.Empty(cut.FindAll("[data-testid='rep-budget-card']"));
        Assert.NotEmpty(cut.FindAll("[data-testid='rep-donut']"));
        // REPORTS-4: pace and trend are month pictures (gone); the bank cuts follow any period (still there).
        Assert.Empty(cut.FindAll("[data-testid='rep-pace-card']"));
        Assert.Empty(cut.FindAll("[data-testid='rep-trend-card']"));
        Assert.Single(cut.FindAll("[data-testid='rep-bank-donut'] [data-testid='chart-legend-item']"));
        Assert.Single(cut.FindAll("[data-testid='rep-method-donut'] [data-testid='chart-legend-item']"));
    }

    // ---- REPORTS-4: pace, trend, banks ----

    [Fact]
    public async Task ChartView_Pace_StepsThroughTheSpendDays_AgainstThePlan_TodayClamped()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, "/api/reports/category-analysis", SingleMonth);
        Http.On(HttpMethod.Get, "/api/reports/months-trend", Trend);

        // Today = 2026-07-15: day 21 of the 35-day window (Jun 25 → Jul 29) → 60 % elapsed; ₡99,704.87 of the ₡150,000 plan → 66 %.
        var cut = Render<Reports>(p => p.Add(x => x.Today, new DateOnly(2026, 7, 15)));
        cut.WaitForElement("[data-testid='rep-budgeted']");
        cut.Find("[data-testid='rep-view-chart']").Click();

        cut.WaitForElement("[data-testid='rep-pace-chart']");
        Assert.Equal(3, cut.FindAll("[data-testid='rep-pace-chart'] [data-testid='chart-point']").Count);
        Assert.Single(cut.FindAll("[data-testid='rep-pace-chart'] [data-testid='chart-plan']"));
        Assert.Single(cut.FindAll("[data-testid='rep-pace-chart'] [data-testid='chart-today']"));
        Assert.Contains("Reports_PaceCaption[60, 66]", cut.Find("[data-testid='rep-pace-caption']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='rep-pace-norate']"));

        // A day past the month clamps to "all elapsed"; the running total is unchanged.
        cut.Render(p => p.Add(x => x.Today, new DateOnly(2026, 9, 5)));
        cut.WaitForAssertion(() => Assert.Contains("Reports_PaceCaption[100, 66]", cut.Find("[data-testid='rep-pace-caption']").TextContent));
    }

    [Fact]
    public async Task ChartView_Trend_OneBarPerMonthOldestFirst_OnAnIncomeTrack_RedWhenOverspent()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, "/api/reports/category-analysis", SingleMonth);
        Http.On(HttpMethod.Get, "/api/reports/months-trend", Trend);

        var cut = Render<Reports>();
        cut.WaitForElement("[data-testid='rep-budgeted']");
        Assert.DoesNotContain(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/reports/months-trend"); // table view never asks for it
        cut.Find("[data-testid='rep-view-chart']").Click();

        cut.WaitForElement("[data-testid='rep-trend-chart']");
        Assert.Contains("count=12", Assert.Single(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/reports/months-trend").RequestUri!.Query);
        var bars = cut.FindAll("[data-testid='rep-trend-chart'] [data-testid='chart-bar']");
        Assert.Equal(3, bars.Count);
        Assert.Equal("true", bars[0].GetAttribute("data-over"));  // May: ₡250,000 spent of ₡200,000
        Assert.Equal("false", bars[1].GetAttribute("data-over")); // June
        Assert.Equal(3, cut.FindAll("[data-testid='rep-trend-chart'] [data-testid='chart-budget']").Count); // every month has an income track
        Assert.Contains("Reports_TrendIncome", cut.Find("[data-testid='rep-trend-chart'] [data-testid='chart-legend']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='rep-trend-norate']"));
    }

    [Fact]
    public async Task ChartView_Trend_NoRate_ShowsSpendOnly_AndSaysSo()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, "/api/reports/category-analysis", NoRate);
        Http.On(HttpMethod.Get, "/api/reports/months-trend", TrendNoRate);

        var cut = Render<Reports>();
        cut.WaitForElement("[data-testid='rep-budgeted']");
        cut.Find("[data-testid='rep-view-chart']").Click();

        cut.WaitForElement("[data-testid='rep-trend-chart']");
        Assert.Single(cut.FindAll("[data-testid='rep-trend-chart'] [data-testid='chart-bar']"));
        Assert.Empty(cut.FindAll("[data-testid='rep-trend-chart'] [data-testid='chart-budget']"));
        Assert.Contains("Reports_TrendNoRate", cut.Find("[data-testid='rep-trend-norate']").TextContent);
        // The pace chart still draws the actual line, without a plan, and says why.
        Assert.Equal(3, cut.FindAll("[data-testid='rep-pace-chart'] [data-testid='chart-point']").Count);
        Assert.Empty(cut.FindAll("[data-testid='rep-pace-chart'] [data-testid='chart-plan']"));
        Assert.Contains("Reports_PaceNoRate", cut.Find("[data-testid='rep-pace-norate']").TextContent);
    }

    [Fact]
    public async Task ChartView_BankDonuts_NameBanks_FallBackForUnknown_AndSplitCardVsAccount()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, "/api/reports/category-analysis", SingleMonth);
        Http.On(HttpMethod.Get, "/api/reports/months-trend", Trend);

        var cut = Render<Reports>();
        cut.WaitForElement("[data-testid='rep-budgeted']");
        cut.Find("[data-testid='rep-view-chart']").Click();

        cut.WaitForElement("[data-testid='rep-bank-donut']");
        var banks = cut.FindAll("[data-testid='rep-bank-donut'] [data-testid='chart-legend-item']");
        Assert.Equal(2, banks.Count);
        Assert.Contains("BAC", banks[0].TextContent);
        Assert.Contains("₡90,000", banks[0].TextContent);
        Assert.Contains("Reports_UnknownBank", banks[1].TextContent); // a bank with no resolvable name is never a blank label
        var methods = cut.FindAll("[data-testid='rep-method-donut'] [data-testid='chart-legend-item']");
        Assert.Equal(2, methods.Count);
        Assert.Contains("Tx_CreditCard", methods[0].TextContent);
        Assert.Contains("Tx_BankAccount", methods[1].TextContent);
        Assert.Contains("₡19,705", methods[1].TextContent);
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
