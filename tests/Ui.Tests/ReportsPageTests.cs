using System.Net;
using Bunit;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// REPORTS-1..4 UI, presented per SKIN-10: four KPI tiles and the pace chart always visible (month mode), then a
/// Table/Chart toggle over the lower half — the category table behind a class switcher, or the eight charts.
/// Range mode validates and loads without budgets; export POSTs the shown period; the month page exports too.
/// </summary>
public class ReportsPageTests : ComponentTestBase
{
    private const string M1 = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string M2 = "aaaaaaaa-0000-0000-0000-000000000002";
    private const string Months = $$"""[{"id":"{{M2}}","year":2026,"month_number":7},{"id":"{{M1}}","year":2026,"month_number":6}]""";
    private const string SingleMonth = """
        {"period":{"from":"2026-06-25","to":"2026-07-29"},"single_month":true,
         "budgeted":[{"category_id":"bbbbbbbb-0000-0000-0000-000000000001","category_name":"Groceries","total_crc":8000,"total_usd":16,"budgeted_crc":60000,"budgeted_usd":120,"transaction_count":3},
                     {"category_id":"bbbbbbbb-0000-0000-0000-000000000002","category_name":"Housing","total_crc":70000,"total_usd":140,"budgeted_crc":60000,"budgeted_usd":120,"transaction_count":1},
                     {"category_id":"bbbbbbbb-0000-0000-0000-000000000003","category_name":"Other","total_crc":100,"total_usd":0.2,"budgeted_crc":null,"budgeted_usd":null},
                     {"category_id":"bbbbbbbb-0000-0000-0000-000000000005","category_name":"Streaming - Disney","total_crc":8604.87,"total_usd":18.99,"budgeted_crc":0,"budgeted_usd":18.99},
                     {"category_id":"bbbbbbbb-0000-0000-0000-000000000006","category_name":"Streaming - Extra","total_crc":11000,"total_usd":25,"budgeted_crc":0,"budgeted_usd":18.99}],
         "extraordinary":[{"category_id":"bbbbbbbb-0000-0000-0000-000000000004","category_name":"Dining","total_crc":2000,"total_usd":4,"budgeted_crc":null,"budgeted_usd":null}],
         "unplanned_essential":[],
         "income":{"crc":200000,"usd":400},"budget_total":{"crc":150000,"usd":300},
         "budget_by_method":[{"key":"credit_card","label":"credit_card","total_crc":120000,"total_usd":240},{"key":"bank_account","label":"bank_account","total_crc":30000,"total_usd":60}],
         "by_bank":[{"key":"cccccccc-0000-0000-0000-000000000001","label":"BAC","total_crc":90000,"total_usd":180},{"key":"cccccccc-0000-0000-0000-000000000002","label":"","total_crc":9704.87,"total_usd":24.19}],
         "by_method":[{"key":"credit_card","label":"credit_card","total_crc":80000,"total_usd":160},{"key":"bank_account","label":"bank_account","total_crc":19704.87,"total_usd":44.19}],
         "spend_by_day":[{"date":"2026-06-26","total_crc":8000,"total_usd":16},{"date":"2026-07-03","total_crc":70000,"total_usd":140},{"date":"2026-07-20","total_crc":21704.87,"total_usd":48.19}],
         "by_card":[{"key":"eeeeeeee-0000-0000-0000-000000000005","label":"Allan's Visa","total_crc":80000,"total_usd":160},{"key":"none","label":"","total_crc":19704.87,"total_usd":44.19}],
         "income_by_member":[{"kind":"member","member_user_id":"dddddddd-0000-0000-0000-000000000001","name":"Allan","amount":{"crc":150000,"usd":300}},
                             {"kind":"household","member_user_id":null,"name":null,"amount":{"crc":40000,"usd":80}},
                             {"kind":"inflows","member_user_id":null,"name":null,"amount":{"crc":10000,"usd":20}}]}
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
    private const string Pdf = """{"download_url":"/api/files/tok-pdf","file_name":"report-2026-06-25_2026-07-29.pdf","period":{"from":"2026-06-25","to":"2026-07-29"},"expires_in_seconds":900}""";
    private const string Export = """{"download_url":"/api/files/tok-1","file_name":"transactions-2026-09-03.csv","row_count":4,"period":{"from":"2026-06-25","to":"2026-07-29"},"expires_in_seconds":900}""";

    private IRenderedComponent<Reports> RenderMonth(string report = SingleMonth, DateOnly? today = null)
    {
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, "/api/reports/category-analysis", report);
        var cut = Render<Reports>(p => { if (today is { } t) p.Add(x => x.Today, t); });
        cut.WaitForElement("[data-testid='rep-category']");
        return cut;
    }

    private static void ChartView(IRenderedComponent<Reports> cut) => cut.Find("[data-testid='rep-view-chart']").Change(true);

    // ---------------------------------------------------------------- the verdict: tiles + pace

    [Fact]
    public async Task Loads_TheNewestMonth_WithFourTiles_ThePaceChart_AndTheBudgetedTable()
    {
        await SignInAsync();
        var cut = RenderMonth(today: new DateOnly(2026, 7, 15));

        Assert.Contains($"month_id={M2}", Assert.Single(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/reports/category-analysis").RequestUri!.Query);
        Assert.Contains("Reports_SingleMonthNote", cut.Find("[data-testid='rep-period']").TextContent);

        // Total spend = the three classes (99,704.87); budgeted 97,704.87 of it; discretionary 2,000; unplanned nothing.
        Assert.Equal("₡99,704.87", cut.Find("[data-testid='rep-kpi-total-money-primary']").TextContent);
        Assert.Equal("$204.19", cut.Find("[data-testid='rep-kpi-total-money-secondary']").TextContent); // "both": the other side stacked beneath
        Assert.Contains("Reports_OfIncome[50]", cut.Find("[data-testid='rep-kpi-total-sub']").TextContent); // of ₡200,000
        Assert.Equal("₡97,704.87", cut.Find("[data-testid='rep-kpi-budgeted-money-primary']").TextContent);
        Assert.Contains("Reports_OfSpend[98]", cut.Find("[data-testid='rep-kpi-budgeted-sub']").TextContent);
        Assert.Equal("₡2,000.00", cut.Find("[data-testid='rep-kpi-discretionary-money-primary']").TextContent);
        Assert.Contains("Reports_OfSpend[2]", cut.Find("[data-testid='rep-kpi-discretionary-sub']").TextContent);
        Assert.Equal("₡0.00", cut.Find("[data-testid='rep-kpi-unplanned-money-primary']").TextContent);
        Assert.Contains("Reports_OfSpend[0]", cut.Find("[data-testid='rep-kpi-unplanned-sub']").TextContent); // no month summary stubbed → no refundable figure

        // The pace chart is promoted out of the chart view: visible in table view, with its summary line as text.
        // Today = 2026-07-15: day 21 of the 35-day window → 60 % elapsed; ₡99,704.87 of the ₡150,000 plan → 66 %.
        Assert.Equal(3, cut.FindAll("[data-testid='rep-pace-chart'] [data-testid='chart-point']").Count);
        Assert.Single(cut.FindAll("[data-testid='rep-pace-chart'] [data-testid='chart-plan']"));
        Assert.Single(cut.FindAll("[data-testid='rep-pace-chart'] [data-testid='chart-today']"));
        Assert.Contains("Reports_PaceCaption[60, 66]", cut.Find("[data-testid='rep-pace-caption']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='rep-pace-norate']"));
        Assert.Empty(cut.FindAll("[data-testid='rep-donut']")); // table view by default: no charts below

        // The category card opens on the budgeted class, with the budget column, the bar column and the tone.
        Assert.True(cut.Find("[data-testid='rep-class-budgeted']").HasAttribute("checked"));
        var rows = cut.FindAll("[data-testid='rep-category'] [data-testid='rep-row']");
        Assert.Equal(5, rows.Count);
        var actuals = cut.FindAll("[data-testid='rep-category'] [data-testid='rep-actual']");
        Assert.Contains("text-success", actuals[0].ClassName); // Groceries under budget
        Assert.Contains("text-danger", actuals[1].ClassName);  // Housing over
        Assert.DoesNotContain("text-success", actuals[2].ClassName); // no line → no tone
        Assert.DoesNotContain("text-danger", actuals[2].ClassName);
        Assert.Contains("text-success", actuals[3].ClassName); // a $18.99 line judged in dollars: $18.99 spent is not over
        Assert.Contains("text-danger", actuals[4].ClassName);  // $25 spent against $18.99 IS over
        Assert.Contains("—", cut.FindAll("[data-testid='rep-budget']")[2].TextContent);
        Assert.Contains("₡120,000.00", cut.Find("[data-testid='rep-category'] [data-testid='rep-total']").TextContent); // budget total
        var counts = cut.FindAll("[data-testid='rep-category'] [data-testid='rep-count']").Select(c => c.TextContent.Trim()).ToArray();
        Assert.Equal(["3", "1", "0", "0", "0"], counts);
        Assert.Equal("4", cut.Find("[data-testid='rep-category'] [data-testid='rep-count-total']").TextContent.Trim());
        Assert.Contains("Reports_ActualCol", cut.Find("[data-testid='rep-category'] thead").TextContent);
        Assert.Contains("Reports_ColSpentVsBudget", cut.Find("[data-testid='rep-category'] thead").TextContent);

        // The inline bar: fill is spend over budget in the line's own currency, red past it, aria-hidden (the figures are the data).
        var bars = cut.FindAll("[data-testid='rep-category'] [data-testid='rep-bar']");
        Assert.Equal(5, bars.Count);
        Assert.Equal("true", bars[0].GetAttribute("aria-hidden"));
        Assert.Contains("13.33%", bars[0].QuerySelector("[data-testid='rep-bar-fill']")!.GetAttribute("style")); // 8,000 of 60,000
        Assert.Equal("true", bars[1].GetAttribute("data-over"));   // Housing
        Assert.Null(bars[2].QuerySelector("[data-testid='rep-bar-fill']")); // no budget, no bar
        Assert.Equal("false", bars[3].GetAttribute("data-over"));  // $18.99 of $18.99
    }

    [Fact]
    public async Task ClassSwitcher_FiltersTheLoadedRows_WithoutARequest()
    {
        await SignInAsync();
        var cut = RenderMonth();

        cut.Find("[data-testid='rep-class-extraordinary']").Change(true);
        Assert.Contains("Dining", Assert.Single(cut.FindAll("[data-testid='rep-category'] [data-testid='rep-row']")).TextContent);
        Assert.Contains("Reports_SpentCol", cut.Find("[data-testid='rep-category'] thead").TextContent); // "Actual" only beside a Budgeted column
        Assert.DoesNotContain("Reports_ActualCol", cut.Find("[data-testid='rep-category'] thead").TextContent);
        Assert.DoesNotContain("Reports_BudgetedCol", cut.Find("[data-testid='rep-category'] thead").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='rep-budget']"));
        Assert.Empty(cut.FindAll("[data-testid='rep-bar']")); // nothing to compare against

        cut.Find("[data-testid='rep-class-unplanned_essential']").Change(true);
        Assert.Contains("Reports_NoneInClass", cut.Find("[data-testid='rep-category']").TextContent);
        Assert.Single(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/reports/category-analysis"); // the rows were already there
    }

    [Fact]
    public async Task UnplannedTile_ShowsTheRefundableAmount_FromTheMonthSummary_AndTheShareForARange()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, $"/api/months/{M2}/summary", """{"month":{"id":"x","year":2026,"month_number":7,"week_count":5,"week1_start_date":"2026-06-25","last_day":"2026-07-29"},"exchange_rate":500,"rate_unavailable":false,"summary":{"refunds_total":{"crc":38600,"usd":77.2}}}""");
        var cut = RenderMonth();

        cut.WaitForAssertion(() => Assert.Contains("Reports_Refundable[₡38,600.00]", cut.Find("[data-testid='rep-kpi-unplanned-sub']").TextContent));

        Http.On(HttpMethod.Get, "/api/reports/category-analysis", Range);
        cut.Find("[data-testid='rep-mode']").Change("range");
        cut.Find("[data-testid='rep-from']").Change("2026-01-01");
        cut.Find("[data-testid='rep-to']").Change("2026-06-30");
        cut.Find("[data-testid='rep-load']").Click();
        cut.WaitForAssertion(() => Assert.Contains("Reports_MultiMonthNote", cut.Find("[data-testid='rep-period']").TextContent));
        Assert.Contains("Reports_OfSpend[0]", cut.Find("[data-testid='rep-kpi-unplanned-sub']").TextContent); // refunds are per month
        Assert.Empty(cut.FindAll("[data-testid='rep-kpi-total-sub']")); // no income for a range: the figure alone
        Assert.Empty(cut.FindAll("[data-testid='rep-pace-card']"));     // the pace is a month picture
    }

    [Fact]
    public async Task ShowIn_Dollars_ShowsOneSide_KeepsBudgetLinesNative_AndTotalsBudgetsAtTodaysRate()
    {
        // Budgets: Groceries ₡60,000 + Housing ₡60,000 (colón lines), Disney $18.99 + Extra $18.99 (dollar lines); rate 500 both sides.
        // Total in $: 120,000 / 500 + 37.98 = 277.98. In ₡: 120,000 + 37.98 × 500 = 138,990.00.
        await SignInAsync();
        var cut = RenderMonth(SingleMonth
            .Replace("\"budgeted_crc\":60000,\"budgeted_usd\":120", "\"budgeted_crc\":60000,\"budgeted_usd\":0")
            .Replace("\"budget_total\":{\"crc\":150000,\"usd\":300}", "\"budget_total\":{\"crc\":150000,\"usd\":300},\"exchange_rate\":500,\"exchange_rate_buy\":500"));
        Assert.Contains("₡138,990.00 · $277.98", cut.Find("[data-testid='rep-budget-total']").TextContent); // "both" — a converted pair, not a per-side split

        cut.Find("[data-testid='rep-cur-usd']").Click();

        cut.WaitForAssertion(() => Assert.Equal("$277.98", cut.Find("[data-testid='rep-budget-total']").TextContent.Trim()));
        Assert.Equal("$204.19", cut.Find("[data-testid='rep-kpi-total-money-primary']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='rep-kpi-total-money-secondary']")); // one side asked for, one side shown
        var budgets = cut.FindAll("[data-testid='rep-category'] [data-testid='rep-budget']").Select(b => b.TextContent.Trim()).ToArray();
        Assert.Contains("₡60,000.00", budgets); // a colón line stays in colones
        Assert.Contains("$18.99", budgets);     // a dollar line stays in dollars
        var actuals = cut.FindAll("[data-testid='rep-category'] [data-testid='rep-actual']").Select(a => a.TextContent.Trim()).ToArray();
        Assert.All(actuals, a => Assert.StartsWith("$", a));
        cut.Find("[data-testid='rep-class-extraordinary']").Change(true);
        Assert.Contains("$4.00", cut.Find("[data-testid='rep-category']").TextContent);
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "appUi.setPref" && Equals(i.Arguments[0], "display.currency"));

        cut.Find("[data-testid='rep-class-budgeted']").Change(true);
        cut.Find("[data-testid='rep-cur-both']").Click();
        cut.WaitForAssertion(() => Assert.Contains("₡138,990.00 · $277.98", cut.Find("[data-testid='rep-budget-total']").TextContent));
    }

    // ---------------------------------------------------------------- chart view: the eight charts, restyled, pace no longer among them

    [Fact]
    public async Task ChartView_DrawsTheSameRows_WithBudgetOverlays_TheClassDonut_AndACurrencySwitch()
    {
        await SignInAsync();
        var cut = RenderMonth();
        Assert.Empty(cut.FindAll("[data-testid='rep-donut']"));

        ChartView(cut);

        // Same five budgeted rows as bars, sorted by size; only ₡-budgeted lines get a ₡ budget overlay.
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("[data-testid='rep-budgeted-chart'] [data-testid='chart-bar']").Count));
        Assert.Empty(cut.FindAll("[data-testid='rep-category']")); // the table is replaced, not duplicated
        var bars = cut.FindAll("[data-testid='rep-budgeted-chart'] [data-testid='chart-bar']");
        Assert.Contains("Housing", bars[0].TextContent); // largest first (70,000)
        Assert.Equal("true", bars[0].GetAttribute("data-over"));
        Assert.Equal(2, cut.FindAll("[data-testid='rep-budgeted-chart'] [data-testid='chart-budget']").Count);
        Assert.Contains("₡", cut.Find("[data-testid='rep-budgeted-chart-total']").TextContent);
        Assert.Equal(3, cut.FindAll("[data-testid='rep-donut'] [data-testid='chart-legend-item']").Count);
        Assert.Equal(2, cut.FindAll("[data-testid='rep-donut'] [data-testid='chart-slice']").Count); // budgeted + extraordinary; unplanned is empty
        Assert.Contains("Reports_NoneInClass", cut.Find("[data-testid='rep-unplanned']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='rep-pace-card'] [data-testid='rep-donut']")); // the pace card sits above the toggle, once
        Assert.Single(cut.FindAll("[data-testid='rep-pace-chart']"));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "appUi.setPref"); // remembered per device

        // Dollars: values re-label, and now only the $-budgeted lines carry an overlay.
        cut.Find("[data-testid='rep-cur-usd']").Click();
        cut.WaitForAssertion(() => Assert.Contains("$", cut.Find("[data-testid='rep-budgeted-chart-total']").TextContent));
        Assert.Equal(4, cut.FindAll("[data-testid='rep-budgeted-chart'] [data-testid='chart-budget']").Count);
        Assert.Contains("$25.00", cut.FindAll("[data-testid='rep-budgeted-chart'] [data-testid='chart-value']").Select(v => v.TextContent).First(t => t.Contains("25")));

        cut.Find("[data-testid='rep-view-table']").Change(true);
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll("[data-testid='rep-category'] [data-testid='rep-row']").Count)); // and back
    }

    [Fact]
    public async Task ChartView_IncomeDonut_MeasuresTheSpendAgainstTheMonthsIncome()
    {
        await SignInAsync();
        var cut = RenderMonth();
        ChartView(cut);

        // Three class slices + Remaining; the hole says the income, not the sum; remaining = 200,000 − 99,704.87.
        cut.WaitForElement("[data-testid='rep-income-donut']");
        var legend = cut.FindAll("[data-testid='rep-income-donut'] [data-testid='chart-legend-item']");
        Assert.Equal(4, legend.Count);
        Assert.Contains("Reports_Remaining", legend[3].TextContent);
        Assert.Contains("₡100,295", legend[3].TextContent);
        Assert.Equal("₡200,000", cut.Find("[data-testid='rep-income-donut'] [data-testid='chart-center']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='rep-income-over']"));
        Assert.Equal(3, cut.FindAll("[data-testid='rep-donut'] [data-testid='chart-legend-item']").Count);

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
        Assert.Contains("$196", cut.FindAll("[data-testid='rep-income-donut'] [data-testid='chart-legend-item']")[3].TextContent);
        Assert.Contains("$100", cut.FindAll("[data-testid='rep-budget-donut'] [data-testid='chart-legend-item']")[1].TextContent);
    }

    [Fact]
    public async Task ChartView_HasNoIncomeByMemberCard()
    {
        // Owner, 2026-09-17: whose the income is lives in the PDF's table, not in a chart on this page.
        await SignInAsync();
        var cut = RenderMonth();
        ChartView(cut);

        cut.WaitForElement("[data-testid='rep-income-donut']");
        Assert.Empty(cut.FindAll("[data-testid='rep-members-card']"));
    }

    [Fact]
    public async Task ChartView_IncomeDonut_Overspent_HasNoRemainingSlice_AndSaysByHowMuch()
    {
        await SignInAsync();
        var cut = RenderMonth(Overspent);
        ChartView(cut);

        cut.WaitForElement("[data-testid='rep-income-donut']");
        Assert.Equal(2, cut.FindAll("[data-testid='rep-income-donut'] [data-testid='chart-slice']").Count);
        Assert.Contains("₡0", cut.FindAll("[data-testid='rep-income-donut'] [data-testid='chart-legend-item']")[3].TextContent);
        Assert.Equal("₡50,000", cut.Find("[data-testid='rep-income-donut'] [data-testid='chart-center']").TextContent);
        Assert.Contains("Reports_OverBy[₡49,704.87]", cut.Find("[data-testid='rep-income-over']").TextContent);

        Assert.Single(cut.FindAll("[data-testid='rep-budget-donut'] [data-testid='chart-slice']"));
        Assert.Contains("₡0", cut.FindAll("[data-testid='rep-budget-donut'] [data-testid='chart-legend-item']")[1].TextContent);
        Assert.Contains("Reports_BudgetOverBy[₡100,000.00]", cut.Find("[data-testid='rep-budget-over']").TextContent);
    }

    [Fact]
    public async Task ChartView_IncomeDonut_NoRate_SaysSo_AndIsAbsentForARange()
    {
        await SignInAsync();
        var cut = RenderMonth(NoRate);
        Assert.Empty(cut.FindAll("[data-testid='rep-kpi-total-sub']")); // no income → no share on the tile
        ChartView(cut);

        cut.WaitForElement("[data-testid='rep-income-card']");
        Assert.Contains("Reports_IncomeNoRate", cut.Find("[data-testid='rep-income-norate']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='rep-income-donut']"));
        Assert.Contains("Reports_IncomeNoRate", cut.Find("[data-testid='rep-budget-norate']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='rep-budget-donut']"));
        Assert.NotEmpty(cut.FindAll("[data-testid='rep-donut']"));

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
        Assert.Empty(cut.FindAll("[data-testid='rep-pace-card']"));
        Assert.Empty(cut.FindAll("[data-testid='rep-trend-card']"));
        Assert.Single(cut.FindAll("[data-testid='rep-bank-donut'] [data-testid='chart-legend-item']"));
        Assert.Single(cut.FindAll("[data-testid='rep-method-donut'] [data-testid='chart-legend-item']"));
        Assert.Empty(cut.FindAll("[data-testid='rep-method-bars']"));
    }

    [Fact]
    public async Task Pace_ClampsTodayPastTheMonth_AndWithoutARate_DrawsTheActualAndSaysWhy()
    {
        await SignInAsync();
        var cut = RenderMonth(today: new DateOnly(2026, 9, 5)); // past the window: all elapsed, the running total unchanged
        Assert.Contains("Reports_PaceCaption[100, 66]", cut.Find("[data-testid='rep-pace-caption']").TextContent);

        Http.On(HttpMethod.Get, "/api/reports/category-analysis", NoRate);
        cut.Find("[data-testid='rep-month']").Change(M1);
        cut.WaitForElement("[data-testid='rep-pace-norate']");
        Assert.Equal(3, cut.FindAll("[data-testid='rep-pace-chart'] [data-testid='chart-point']").Count);
        Assert.Empty(cut.FindAll("[data-testid='rep-pace-chart'] [data-testid='chart-plan']"));
        Assert.Contains("Reports_PaceNoRate", cut.Find("[data-testid='rep-pace-norate']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='rep-pace-caption']"));
    }

    [Fact]
    public async Task ChartView_Trend_OneBarPerMonthOldestFirst_OnAnIncomeTrack_RedWhenOverspent()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/reports/months-trend", Trend);
        var cut = RenderMonth();
        Assert.DoesNotContain(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/reports/months-trend"); // table view never asks for it
        ChartView(cut);

        cut.WaitForElement("[data-testid='rep-trend-chart']");
        Assert.Contains("count=12", Assert.Single(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/reports/months-trend").RequestUri!.Query);
        var bars = cut.FindAll("[data-testid='rep-trend-chart'] [data-testid='chart-bar']");
        Assert.Equal(3, bars.Count);
        Assert.Equal("true", bars[0].GetAttribute("data-over"));  // May: ₡250,000 spent of ₡200,000
        Assert.Equal("false", bars[1].GetAttribute("data-over")); // June
        Assert.Equal(3, cut.FindAll("[data-testid='rep-trend-chart'] [data-testid='chart-budget']").Count);
        Assert.Contains("Reports_TrendIncome", cut.Find("[data-testid='rep-trend-chart'] [data-testid='chart-legend']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='rep-trend-norate']"));
    }

    [Fact]
    public async Task ChartView_Trend_NoRate_ShowsSpendOnly_AndSaysSo()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/reports/months-trend", TrendNoRate);
        var cut = RenderMonth(NoRate);
        ChartView(cut);

        cut.WaitForElement("[data-testid='rep-trend-chart']");
        Assert.Single(cut.FindAll("[data-testid='rep-trend-chart'] [data-testid='chart-bar']"));
        Assert.Empty(cut.FindAll("[data-testid='rep-trend-chart'] [data-testid='chart-budget']"));
        Assert.Contains("Reports_TrendNoRate", cut.Find("[data-testid='rep-trend-norate']").TextContent);
    }

    [Fact]
    public async Task ChartView_BankDonuts_NameBanks_FallBackForUnknown_AndSplitCardVsAccount()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/reports/months-trend", Trend);
        var cut = RenderMonth();
        ChartView(cut);

        cut.WaitForElement("[data-testid='rep-bank-donut']");
        var banks = cut.FindAll("[data-testid='rep-bank-donut'] [data-testid='chart-legend-item']");
        Assert.Equal(2, banks.Count);
        Assert.Contains("BAC", banks[0].TextContent);
        Assert.Contains("₡90,000", banks[0].TextContent);
        Assert.Contains("Reports_UnknownBank", banks[1].TextContent);
        var methods = cut.FindAll("[data-testid='rep-method-donut'] [data-testid='chart-legend-item']");
        Assert.Equal(2, methods.Count);
        Assert.Contains("Tx_CreditCard", methods[0].TextContent);
        Assert.Contains("Tx_BankAccount", methods[1].TextContent);
        Assert.Contains("₡19,705", methods[1].TextContent);
        var cardSlices = cut.FindAll("[data-testid='rep-card-donut'] [data-testid='chart-legend-item']");
        Assert.Equal(2, cardSlices.Count);
        Assert.Contains("Allan's Visa", cardSlices[0].TextContent);
        Assert.Contains("₡80,000", cardSlices[0].TextContent);
        Assert.Contains("Tx_NoCard", cardSlices[1].TextContent);
        var bars = cut.FindAll("[data-testid='rep-method-bars'] [data-testid='chart-bar']");
        Assert.Equal(2, bars.Count);
        Assert.Contains("Tx_CreditCard", bars[0].TextContent);
        Assert.Contains("₡80,000", bars[0].QuerySelector("[data-testid='chart-value']")!.TextContent);
        Assert.NotNull(bars[0].QuerySelector("[data-testid='chart-budget']"));
        Assert.Equal("false", bars[0].GetAttribute("data-over"));
        var captions = cut.FindAll("[data-testid='rep-method-caption']");
        Assert.Equal(2, captions.Count);
        Assert.Contains("Reports_MethodCaption[₡120,000.00, ₡80,000.00]", captions[0].TextContent);
    }

    // ---------------------------------------------------------------- range, export, empty

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
        Assert.Empty(cut.FindAll("[data-testid='rep-bar']"));
        Assert.Empty(cut.FindAll("[data-testid='rep-pace-card']"));
        Assert.Equal("₡8,000.00", cut.Find("[data-testid='rep-kpi-total-money-primary']").TextContent); // the tiles still add the period up
    }

    [Fact]
    public async Task Export_PostsTheShownPeriod_AndLaunchesTheAbsoluteLink()
    {
        await SignInAsync();
        Http.On(HttpMethod.Post, "/api/reports/transactions/export", Export);
        var cut = RenderMonth();
        cut.Find("[data-testid='rep-export']").Click();

        cut.WaitForElement("[data-testid='rep-notice']");
        var post = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/reports/transactions/export");
        Assert.Contains($"month_id={M2}", post.RequestUri!.Query);
        var launched = Assert.Single(Downloads.Launched);
        Assert.Equal(("http://localhost/api/files/tok-1", "transactions-2026-09-03.csv"), launched);
        Assert.Contains("Reports_ExportReady[4]", cut.Find("[data-testid='rep-notice']").TextContent);
    }

    // ---------------------------------------------------------------- REPORTS-7: the PDF

    private static async Task<System.Text.Json.JsonElement> PdfBodyAsync(HttpRequestMessage post) =>
        System.Text.Json.JsonDocument.Parse(await post.Content!.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Pdf_SendsTheShownMonth_AndTheScreensChoices_ThenLaunchesTheLink()
    {
        await SignInAsync();
        Http.On(HttpMethod.Post, "/api/reports/pdf", Pdf);
        var cut = RenderMonth(today: new DateOnly(2026, 7, 15));

        cut.Find("[data-testid='rep-pdf']").Click();
        var summary = cut.Find("[data-testid='rep-pdf-summary']").TextContent;
        Assert.Contains("Reports_PdfAmounts[Reports_PdfBoth]", summary); // the "show in" default is both sides
        Assert.Contains("Reports_PdfCharts[₡]", summary);
        Assert.NotEmpty(cut.FindAll("[data-testid='rep-pdf-columns']"));
        cut.Find("[data-testid='rep-pdf-appendix']").Change(false);
        Assert.Empty(cut.FindAll("[data-testid='rep-pdf-columns']")); // no transactions, no columns to pick
        cut.Find("[data-testid='rep-pdf-download']").Click();

        cut.WaitForElement("[data-testid='rep-notice']");
        var body = await PdfBodyAsync(Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/reports/pdf"));
        Assert.Equal(M2, body.GetProperty("month_id").GetString());
        Assert.False(body.TryGetProperty("from", out _));
        Assert.Equal("both", body.GetProperty("display").GetString());
        Assert.Equal("CRC", body.GetProperty("chart_currency").GetString());
        Assert.False(body.GetProperty("include_appendix").GetBoolean());
        Assert.False(body.TryGetProperty("appendix_columns", out _));
        Assert.False(body.TryGetProperty("language", out _)); // the API reads the language saved in the account
        Assert.Equal("2026-07-15", body.GetProperty("today").GetString());
        Assert.Equal(("http://localhost/api/files/tok-pdf", "report-2026-06-25_2026-07-29.pdf"), Assert.Single(Downloads.Launched));
        Assert.Empty(cut.FindAll("[data-testid='rep-pdf-dialog']"));
        Assert.Contains("Reports_PdfReady", cut.Find("[data-testid='rep-notice']").TextContent);
    }

    [Fact]
    public async Task Pdf_Columns_AllTickedByDefault_AndALeanerChoiceIsSent_ToDownloadAndEmail()
    {
        await SignInAsync();
        Http.On(HttpMethod.Post, "/api/reports/pdf", Pdf);
        Http.On(HttpMethod.Post, "/api/reports/pdf/email",
            """{"sent_to":"ana@example.com","file_name":"r.pdf","period":{"from":"2026-06-25","to":"2026-07-29"}}""", HttpStatusCode.Accepted);
        var cut = RenderMonth();

        cut.Find("[data-testid='rep-pdf']").Click();
        var boxes = cut.FindAll("[data-testid='rep-pdf-columns'] input[type='checkbox']");
        Assert.Equal(9, boxes.Count);
        Assert.All(boxes, b => Assert.True(b.HasAttribute("checked")));
        Assert.Contains("Reports_PdfColumnsHint", cut.Find("[data-testid='rep-pdf-columns']").TextContent);
        cut.Find("[data-testid='rep-pdf-download']").Click();
        cut.WaitForElement("[data-testid='rep-notice']");
        var all = await PdfBodyAsync(Http.Requests.Last(r => r.RequestUri!.AbsolutePath == "/api/reports/pdf"));
        Assert.Equal(["category", "class", "amount", "rate", "method", "bank", "source", "card", "notes"],
            all.GetProperty("appendix_columns").EnumerateArray().Select(e => e.GetString()));

        cut.Find("[data-testid='rep-pdf']").Click();
        foreach (var key in new[] { "rate", "source", "card", "class" })
            cut.Find($"[data-testid='rep-pdf-col-{key}']").Change(false);
        Assert.False(cut.Find("[data-testid='rep-pdf-col-rate']").HasAttribute("checked"));
        cut.Find("[data-testid='rep-pdf-email']").Click();
        cut.WaitForAssertion(() => Assert.Contains(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/reports/pdf/email"));
        var lean = await PdfBodyAsync(Http.Requests.Last(r => r.RequestUri!.AbsolutePath == "/api/reports/pdf/email"));
        Assert.Equal(["category", "amount", "method", "bank", "notes"], lean.GetProperty("appendix_columns").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Pdf_ForARange_SendsTheDates_AndTheCurrencyShown()
    {
        await SignInAsync();
        Http.On(HttpMethod.Post, "/api/reports/pdf", Pdf);
        var cut = RenderMonth();
        cut.Find("[data-testid='rep-cur-usd']").Click(); // "show in" $ — the charts follow
        Http.On(HttpMethod.Get, "/api/reports/category-analysis", Range);
        cut.Find("[data-testid='rep-mode']").Change("range");
        Assert.True(cut.Find("[data-testid='rep-pdf']").HasAttribute("disabled")); // nothing loaded for the range yet
        cut.Find("[data-testid='rep-from']").Change("2026-01-01");
        cut.Find("[data-testid='rep-to']").Change("2026-06-30");
        cut.Find("[data-testid='rep-load']").Click();
        cut.WaitForElement("[data-testid='rep-category']");

        cut.Find("[data-testid='rep-pdf']").Click();
        Assert.Contains("Reports_PdfAmounts[$]", cut.Find("[data-testid='rep-pdf-summary']").TextContent);
        cut.Find("[data-testid='rep-pdf-download']").Click();

        cut.WaitForElement("[data-testid='rep-notice']");
        var body = await PdfBodyAsync(Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/reports/pdf"));
        Assert.False(body.TryGetProperty("month_id", out _));
        Assert.Equal(("2026-01-01", "2026-06-30"), (body.GetProperty("from").GetString(), body.GetProperty("to").GetString()));
        Assert.Equal(("USD", "USD"), (body.GetProperty("display").GetString(), body.GetProperty("chart_currency").GetString()));
        Assert.True(body.GetProperty("include_appendix").GetBoolean());
    }

    [Fact]
    public async Task Pdf_Failure_KeepsTheDialogOpen_WithTheError_AndCancelCloses()
    {
        await SignInAsync();
        Http.On(HttpMethod.Post, "/api/reports/pdf", "{}", HttpStatusCode.InternalServerError);
        var cut = RenderMonth();

        cut.Find("[data-testid='rep-pdf']").Click();
        cut.Find("[data-testid='rep-pdf-download']").Click();

        cut.WaitForElement("[data-testid='rep-pdf-error']");
        Assert.Contains("Reports_PdfError", cut.Find("[data-testid='rep-pdf-error']").TextContent);
        Assert.Empty(Downloads.Launched);
        cut.Find("[data-testid='rep-pdf-cancel']").Click();
        Assert.Empty(cut.FindAll("[data-testid='rep-pdf-dialog']"));
        Assert.Empty(cut.FindAll("[data-testid='rep-notice']"));
    }

    // ---------------------------------------------------------------- REPORTS-8: email me

    [Fact]
    public async Task EmailMe_SendsTheSameChoices_AndSaysWhereItWent()
    {
        await SignInAsync();
        Http.On(HttpMethod.Post, "/api/reports/pdf/email",
            """{"sent_to":"ana@example.com","file_name":"report-2026-06-25_2026-07-29.pdf","period":{"from":"2026-06-25","to":"2026-07-29"}}""",
            HttpStatusCode.Accepted);
        var cut = RenderMonth(today: new DateOnly(2026, 7, 15));

        cut.Find("[data-testid='rep-pdf']").Click();
        cut.Find("[data-testid='rep-pdf-appendix']").Change(false);
        cut.Find("[data-testid='rep-pdf-email']").Click();

        cut.WaitForElement("[data-testid='rep-notice']");
        var body = await PdfBodyAsync(Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/reports/pdf/email"));
        Assert.Equal(M2, body.GetProperty("month_id").GetString());
        Assert.False(body.GetProperty("include_appendix").GetBoolean());
        Assert.Equal("2026-07-15", body.GetProperty("today").GetString());
        Assert.False(body.TryGetProperty("language", out _));
        Assert.Empty(Downloads.Launched); // nothing downloads: it went to the inbox
        Assert.Empty(cut.FindAll("[data-testid='rep-pdf-dialog']"));
        Assert.Contains("Reports_PdfSent[ana@example.com]", cut.Find("[data-testid='rep-notice']").TextContent);
    }

    [Fact]
    public async Task EmailMe_PastTheDailyCap_SaysSo_AndKeepsTheDialog()
    {
        await SignInAsync();
        Http.On(HttpMethod.Post, "/api/reports/pdf/email", "", HttpStatusCode.TooManyRequests);
        var cut = RenderMonth();

        cut.Find("[data-testid='rep-pdf']").Click();
        cut.Find("[data-testid='rep-pdf-email']").Click();

        cut.WaitForElement("[data-testid='rep-pdf-error']");
        Assert.Contains("Reports_PdfEmailLimit", cut.Find("[data-testid='rep-pdf-error']").TextContent);
        Assert.NotEmpty(cut.FindAll("[data-testid='rep-pdf-dialog']"));
    }

    [Fact]
    public async Task EmailMe_Failure_KeepsTheDialog_WithTheError()
    {
        await SignInAsync();
        Http.On(HttpMethod.Post, "/api/reports/pdf/email", """{"error":"report_too_large","message":"x"}""", HttpStatusCode.BadRequest);
        var cut = RenderMonth();

        cut.Find("[data-testid='rep-pdf']").Click();
        cut.Find("[data-testid='rep-pdf-email']").Click();

        cut.WaitForElement("[data-testid='rep-pdf-error']");
        Assert.Contains("Reports_PdfEmailError", cut.Find("[data-testid='rep-pdf-error']").TextContent);
    }

    [Fact]
    public async Task NoMonths_DisablesExport_AndShowsTheEmptyHint()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", "[]");

        var cut = Render<Reports>();

        cut.WaitForElement("[data-testid='rep-no-months']");
        Assert.True(cut.Find("[data-testid='rep-export']").HasAttribute("disabled"));
        Assert.True(cut.Find("[data-testid='rep-pdf']").HasAttribute("disabled"));
        Assert.DoesNotContain(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/reports/category-analysis");
        Assert.Empty(cut.FindAll("[data-testid='rep-kpi-total']")); // nothing to add up yet
    }

    [Fact]
    public async Task MonthPage_ExportButton_PostsThatMonth_AndLaunches()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, $"/api/months/{M1}", $$"""{"id":"{{M1}}","year":2026,"month_number":6,"week_count":4,"week1_start_date":"2026-05-28","income_rows":[],"weeks":[{"week_number":1,"start_date":"2026-05-28","end_date":"2026-06-03"}]}""");
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
