using System.Net;
using Bunit;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>DASH-1 UI: picks the newest month, renders the cards/tables with green/red budget tone, shows the empty state, and blocks projections when no rate resolves.</summary>
public class DashboardPageTests : ComponentTestBase
{
    private const string M1 = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string M2 = "aaaaaaaa-0000-0000-0000-000000000002";
    private const string Months = $$"""[{"id":"{{M2}}","year":2026,"month_number":7},{"id":"{{M1}}","year":2026,"month_number":6}]""";

    private static string Money(decimal crc, decimal usd) => $$"""{"crc":{{crc}},"usd":{{usd}}}""";

    private static string Dash(string monthId, bool rateUnavailable = false) => $$"""
        {"month":{"id":"{{monthId}}","year":2026,"month_number":7,"week_count":5,"week1_start_date":"2026-06-25","last_day":"2026-07-29"},
         "exchange_rate":{{(rateUnavailable ? "null" : "500")}},"rate_source":{{(rateUnavailable ? "null" : "\"cache\"")}},"rate_as_of":"2026-09-03T12:00:00+00:00","rate_unavailable":{{(rateUnavailable ? "true" : "false")}},
         "summary":{{(rateUnavailable ? "null" : Summary)}}}
        """;

    private const string Summary = """
        {"income_primary":{"crc":1500000,"usd":3000},"income_secondary":{"crc":0,"usd":0},"income_total":{"crc":1500000,"usd":3000},
         "expenses_card":{"crc":10000,"usd":20},"expenses_account":{"crc":300000,"usd":600},"expenses_total":{"crc":310000,"usd":620},"expenses_remainder":{"crc":1190000,"usd":2380},
         "spent_budgeted":{"crc":300000,"usd":600},"spent_extraordinary":{"crc":0,"usd":0},"spent_unplanned":{"crc":10000,"usd":20},
         "fixed_expenses":[{"name":"Mortgage","budget":{"crc":350000,"usd":700},"actual":{"crc":300000,"usd":600}},{"name":"Water","budget":{"crc":15000,"usd":30},"actual":{"crc":18000,"usd":36}}],
         "variable_expenses":[],
         "other_spending":[{"category_name":"Dining","actual":{"crc":10000,"usd":20}}],
         "weekly_budgeted":[{"week_number":1,"start_date":"2026-06-25","end_date":"2026-07-01","total":{"crc":0,"usd":0}},{"week_number":2,"start_date":"2026-07-02","end_date":"2026-07-08","total":{"crc":300000,"usd":600}}],
         "weekly_extraordinary":[{"week_number":1,"start_date":"2026-06-25","end_date":"2026-07-01","total":{"crc":0,"usd":0}},{"week_number":2,"start_date":"2026-07-02","end_date":"2026-07-08","total":{"crc":0,"usd":0}}],
         "current_balance":{"crc":1190000,"usd":2380},"remainder_for_debts":{"crc":1150000,"usd":2300},"pending_budgeted":{"crc":50000,"usd":100},"actual_remainder":{"crc":1140000,"usd":2280},
         "unplanned_essential_total":{"crc":10000,"usd":20},"refunds_total":{"crc":5000,"usd":10},
         "envelope_reminders":[{"name":"Marchamo","annual_target":{"crc":718000,"usd":0},"contributed_this_month":{"crc":0,"usd":0},"remaining":{"crc":718000,"usd":0},"cadence":"monthly"}],
         "bank_method_breakdown":[{"bank_id":"cccccccc-0000-0000-0000-000000000003","bank_name":"BAC","payment_method":"bank_account","budget":{"crc":365000,"usd":730},"actual":{"crc":300000,"usd":600}},{"bank_id":null,"bank_name":"","payment_method":"credit_card","budget":{"crc":0,"usd":0},"actual":{"crc":10000,"usd":20}}],
         "by_card":[{"card_id":"eeeeeeee-0000-0000-0000-000000000005","card_name":"Allan's Visa","actual":{"crc":10000,"usd":20},"count":1},{"card_id":null,"card_name":"","actual":{"crc":300000,"usd":600},"count":1}]}
        """;

    [Fact]
    public async Task Waterfall_ForecastBelowZero_IsRed_AndSaysThePlanDoesNotFit_PaceClampsToTheMonth()
    {
        // Still planned ₡1,300,000 against ₡1,190,000 left → forecast −₡110,000.
        var overPlan = Summary.Replace("\"pending_budgeted\":{\"crc\":50000,\"usd\":100},\"actual_remainder\":{\"crc\":1140000,\"usd\":2280}",
            "\"pending_budgeted\":{\"crc\":1300000,\"usd\":2600},\"actual_remainder\":{\"crc\":-110000,\"usd\":-220}");
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, $"/api/months/{M2}/summary", Dash(M2).Replace(Summary, overPlan));

        // Today = Jul 15 of a Jun 25 – Jul 29 month → day 21 of 35 → 60 %.
        var cut = Render<Dashboard>(p => p.Add(x => x.Today, new DateOnly(2026, 7, 15)));
        cut.WaitForElement("[data-testid='dash-wf-forecast']");

        var forecast = cut.Find("[data-testid='dash-wf-forecast']");
        Assert.Contains("₡-110,000.00", forecast.TextContent);
        Assert.Contains("text-danger", forecast.QuerySelector("span.text-end")!.ClassName);
        Assert.Contains("Dash_WfOverPlan", cut.Find("[data-testid='dash-wf-overplan']").TextContent);
        Assert.Contains("Dash_WfPace[60]", cut.Find("[data-testid='dash-wf-planned-hint']").TextContent);
        // The bar paints the ₡110,000 shortfall red past the income and lists it; no forecast segment is drawn.
        Assert.Single(cut.FindAll("[data-testid='dash-bar'] [data-testid='chart-overflow']"));
        Assert.Contains("₡110,000", cut.Find("[data-testid='dash-bar'] [data-testid='chart-legend-over']").TextContent);
        Assert.DoesNotContain(cut.FindAll("[data-testid='dash-bar'] [data-testid='chart-segment']"), e => e.GetAttribute("data-label") == "Dash_BarForecast");

        cut.Render(p => p.Add(x => x.Today, new DateOnly(2026, 9, 5)));
        cut.WaitForAssertion(() => Assert.Contains("Dash_WfPace[100]", cut.Find("[data-testid='dash-wf-planned-hint']").TextContent));
    }

    [Fact]
    public async Task LinesAreJudgedInTheirOwnCurrency_TotalsOnlyWhenOverOnBothSides()
    {
        // A $2.99 line paid at $2.99: its ₡ budget (today's rate) is a few colones under the frozen ₡ actual — never red.
        var usdLines = Summary.Replace("\"variable_expenses\":[]",
            "\"variable_expenses\":[{\"name\":\"Apple\",\"budget\":{\"crc\":1355.30,\"usd\":2.99},\"actual\":{\"crc\":1355.35,\"usd\":2.99},\"budget_currency\":\"USD\"},"
            + "{\"name\":\"Netflix\",\"budget\":{\"crc\":1355.30,\"usd\":2.99},\"actual\":{\"crc\":1586.50,\"usd\":3.50},\"budget_currency\":\"USD\"}]");
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, $"/api/months/{M2}/summary", Dash(M2).Replace(Summary, usdLines));

        var cut = Render<Dashboard>();
        cut.WaitForElement("[data-testid='dash-variable']");

        var actuals = cut.FindAll("[data-testid='dash-variable'] [data-testid='dash-line-actual']");
        Assert.Contains("text-success", actuals[0].ClassName); // Apple: $2.99 of $2.99 — the ₡ side used to paint it red
        Assert.Contains("text-danger", actuals[1].ClassName);  // Netflix: $3.50 of $2.99
        // The total is a converted pair: over on both sides here (₡2,941.85 > ₡2,710.60 and $6.49 > $5.98) → red.
        Assert.Contains("text-danger", cut.Find("[data-testid='dash-variable'] [data-testid='dash-lines-total']").QuerySelectorAll("td")[2].ClassName);
    }

    [Fact]
    public async Task ShowIn_FollowsTheAccount_AndSavesThere()
    {
        // ADR-V020: the account's choice wins over the device's (this device never chose); a new choice is PUT to the account.
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, $"/api/months/{M2}/summary", Dash(M2));
        Http.On(HttpMethod.Get, "/api/display-settings", """{"display_currency":"USD","is_default":false}""");
        Http.On(HttpMethod.Put, "/api/display-settings", """{"display_currency":"both","is_default":false}""");

        var cut = Render<Dashboard>();

        cut.WaitForAssertion(() => Assert.Contains("$3,000.00", cut.Find("[data-testid='dash-wf-income']").TextContent));
        Assert.DoesNotContain("₡", cut.Find("[data-testid='dash-wf-income']").TextContent);
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "appUi.setPref" && Equals(i.Arguments[0], "display.currency") && Equals(i.Arguments[1], "USD")); // the device copy is refreshed

        cut.Find("[data-testid='dash-cur-both']").Click();

        cut.WaitForAssertion(() => Assert.Contains("₡1,500,000.00 · $3,000.00", cut.Find("[data-testid='dash-wf-income']").TextContent));
        var put = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath == "/api/display-settings");
        Assert.Contains("\"display_currency\":\"both\"", await put.Content!.ReadAsStringAsync());
    }

    [Fact]
    public async Task ShowIn_Dollars_ShowsOneSide_ButABudgetLineKeepsItsOwnCurrency()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, $"/api/months/{M2}/summary", Dash(M2));
        var cut = Render<Dashboard>();
        cut.WaitForElement("[data-testid='dash-waterfall']");

        cut.Find("[data-testid='dash-cur-usd']").Click();

        cut.WaitForAssertion(() => Assert.Contains("$3,000.00", cut.Find("[data-testid='dash-wf-income']").TextContent));
        Assert.DoesNotContain("₡", cut.Find("[data-testid='dash-wf-income']").TextContent);
        var mortgage = cut.FindAll("[data-testid='dash-fixed'] [data-testid='dash-line-row']")[0].QuerySelectorAll("td");
        Assert.Equal("₡350,000.00", mortgage[1].TextContent.Trim()); // set in colones → stays in colones
        Assert.Equal("$600.00", mortgage[2].TextContent.Trim());     // the actual follows the switch
        Assert.Contains("$", cut.Find("[data-testid='dash-fixed'] [data-testid='dash-lines-total']").TextContent);
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "appUi.setPref" && Equals(i.Arguments[0], "display.currency"));
        Assert.Contains("$2,280", cut.FindAll("[data-testid='dash-bar'] [data-testid='chart-legend-item']")[4].TextContent); // the bar follows a one-currency pick

        cut.Find("[data-testid='dash-cur-both']").Click();
        cut.WaitForAssertion(() => Assert.Contains("₡1,500,000.00 · $3,000.00", cut.Find("[data-testid='dash-wf-income']").TextContent));
        Assert.Contains("₡1,140,000 · $2,280", cut.FindAll("[data-testid='dash-bar'] [data-testid='chart-legend-item']")[4].TextContent); // the bar's legend carries both sides
        Assert.Empty(cut.FindAll("[data-testid='dash-bar-usd']")); // the card has no switch of its own
    }

    [Fact]
    public async Task Income_ShowsInflowsAsTheirOwnSubRow_SoTheSubRowsAddUp()
    {
        // The API folds inflows into income_total; the page derives "Other income" = total − primary − secondary.
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, $"/api/months/{M2}/summary", Dash(M2).Replace("\"income_total\":{\"crc\":1500000,\"usd\":3000}", "\"income_total\":{\"crc\":1550000,\"usd\":3100}"));

        var cut = Render<Dashboard>();

        cut.WaitForElement("[data-testid='dash-waterfall']");
        Assert.Contains("₡50,000.00 · $100.00", cut.Find("[data-testid='dash-wf-income-other']").TextContent);
        Assert.Contains("Dash_WfIncomeOther", cut.Find("[data-testid='dash-wf-income-other']").TextContent);

        Http.On(HttpMethod.Get, $"/api/months/{M2}/summary", Dash(M2)); // no inflows → no row
        var plain = Render<Dashboard>();
        plain.WaitForElement("[data-testid='dash-waterfall']");
        Assert.Empty(plain.FindAll("[data-testid='dash-wf-income-other']"));
    }

    [Fact]
    public async Task Loads_TheNewestMonth_AndRendersEverySection()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, $"/api/months/{M2}/summary", Dash(M2));

        var cut = Render<Dashboard>();

        cut.WaitForElement("[data-testid='dash-waterfall']");
        Assert.Single(Http.Requests, r => r.RequestUri!.AbsolutePath == $"/api/months/{M2}/summary"); // newest first
        Assert.Contains("₡1,500,000.00 · $3,000.00", cut.Find("[data-testid='dash-wf-income']").TextContent);
        Assert.Contains("₡1,500,000.00", cut.Find("[data-testid='dash-wf-income-primary']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='dash-wf-income-secondary']")); // zero secondary income stays out of the way
        Assert.Empty(cut.FindAll("[data-testid='dash-income']")); // the Income card is folded into the waterfall
        // The bar: income wide, filled by the three classes + still planned, the forecast as the green rest; no shortfall; today marked.
        var segments = cut.FindAll("[data-testid='dash-bar'] [data-testid='chart-segment']").Select(e => e.GetAttribute("data-label") ?? "").ToArray();
        Assert.Equal(["Tx_Budgeted", "Tx_Unplanned", "Dash_BarPlanned", "Dash_BarForecast"], segments); // discretionary is ₡0 → listed, not drawn
        Assert.Equal(5, cut.FindAll("[data-testid='dash-bar'] [data-testid='chart-legend-item']").Count);
        Assert.Empty(cut.FindAll("[data-testid='dash-bar'] [data-testid='chart-overflow']"));
        Assert.Single(cut.FindAll("[data-testid='dash-bar'] [data-testid='chart-marker']"));
        Assert.Contains("₡1,140,000", cut.FindAll("[data-testid='dash-bar'] [data-testid='chart-legend-item']")[4].TextContent); // forecast segment = 76 % of income
        cut.Find("[data-testid='dash-cur-usd']").Click(); // the page's "show in" drives the bar
        cut.WaitForAssertion(() => Assert.Contains("$2,280", cut.FindAll("[data-testid='dash-bar'] [data-testid='chart-legend-item']")[4].TextContent));
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "appUi.setPref"); // remembered per device, shared with Reports
        cut.Find("[data-testid='dash-cur-both']").Click(); // back to both sides for the rest of the page
        cut.WaitForAssertion(() => Assert.Contains("₡1,140,000 · $2,280", cut.FindAll("[data-testid='dash-bar'] [data-testid='chart-legend-item']")[4].TextContent));
        // The waterfall (ADR-V018): income − the three classes = spent; income − spent = left now; left − still planned = forecast.
        Assert.Contains("₡1,500,000.00", cut.Find("[data-testid='dash-wf-income']").TextContent);
        Assert.Contains("₡300,000.00", cut.Find("[data-testid='dash-wf-budgeted']").TextContent);
        Assert.Contains("₡0.00", cut.Find("[data-testid='dash-wf-discretionary']").TextContent);
        Assert.Contains("₡10,000.00", cut.Find("[data-testid='dash-wf-unplanned']").TextContent);
        Assert.Contains("₡310,000.00", cut.Find("[data-testid='dash-wf-spent']").TextContent);
        Assert.Contains("₡1,190,000.00", cut.Find("[data-testid='dash-wf-left']").TextContent);
        Assert.Contains("₡50,000.00", cut.Find("[data-testid='dash-wf-planned']").TextContent);
        Assert.Contains("Dash_WfPace[", cut.Find("[data-testid='dash-wf-planned-hint']").TextContent);
        var forecast = cut.Find("[data-testid='dash-wf-forecast']");
        Assert.Contains("₡1,140,000.00", forecast.TextContent);
        Assert.Contains("text-success", forecast.QuerySelector("span.text-end")!.ClassName);
        Assert.Empty(cut.FindAll("[data-testid='dash-wf-overplan']"));
        Assert.Empty(cut.FindAll("[data-testid='dash-balance']")); // the old Expenses/Balance cards are gone
        Assert.Empty(cut.FindAll("[data-testid='dash-rate']")); // the rate is shown once, in the Today's-rate badge

        var actuals = cut.FindAll("[data-testid='dash-line-actual']");
        Assert.Contains("text-success", actuals[0].ClassName); // Mortgage under budget
        Assert.Contains("text-danger", actuals[1].ClassName);  // Water over budget

        Assert.Contains("Dining", cut.Find("[data-testid='dash-other']").TextContent);
        Assert.Equal(2, cut.FindAll("[data-testid='dash-week-row']").Count);

        // Total rows: the sum of exactly the rows shown, each side in its own currency; the lines total keeps the over/under tone.
        var fixedTotal = cut.Find("[data-testid='dash-fixed'] [data-testid='dash-lines-total']");
        Assert.Contains("₡365,000.00 · $730.00", fixedTotal.TextContent); // Mortgage 350,000 + Water 15,000
        Assert.Contains("₡318,000.00 · $636.00", fixedTotal.TextContent); // 300,000 + 18,000 actual
        Assert.Contains("text-success", fixedTotal.QuerySelectorAll("td")[2].ClassName); // under budget overall
        Assert.Empty(cut.FindAll("[data-testid='dash-variable'] [data-testid='dash-lines-total']")); // no lines → no table, no total
        Assert.Contains("₡10,000.00 · $20.00", cut.Find("[data-testid='dash-other-total']").TextContent);
        var weekTotal = cut.Find("[data-testid='dash-week-total']").QuerySelectorAll("td");
        Assert.Contains("₡300,000.00 · $600.00", weekTotal[1].TextContent);
        Assert.Contains("₡0.00 · $0.00", weekTotal[2].TextContent);
        Assert.Contains("Marchamo", cut.Find("[data-testid='dash-envelopes']").TextContent);
        // Grouped by method (card first), a subtotal per method, a grand total: "budgeted on cards vs the account" reads off the table.
        var bankRows = cut.FindAll("[data-testid='dash-bank-row']");
        Assert.Contains("Budget_Unassigned", bankRows[0].TextContent); // the credit-card group (Unassigned · card) now comes first
        Assert.Contains("BAC", bankRows[1].TextContent);
        var methodTotals = cut.FindAll("[data-testid='dash-method-total']");
        Assert.Equal(["credit_card", "bank_account"], methodTotals.Select(r => r.GetAttribute("data-method")));
        Assert.Contains("Dash_MethodTotal[Tx_CreditCard]", methodTotals[0].TextContent);
        Assert.Contains("₡0.00 · $0.00", methodTotals[0].QuerySelectorAll("td")[1].TextContent);       // nothing budgeted on cards
        Assert.Contains("₡10,000.00 · $20.00", methodTotals[0].QuerySelectorAll("td")[2].TextContent); // but the lunch was paid by card
        Assert.Contains("₡365,000.00 · $730.00", methodTotals[1].QuerySelectorAll("td")[1].TextContent);
        Assert.Contains("₡300,000.00 · $600.00", methodTotals[1].QuerySelectorAll("td")[2].TextContent);
        var banksTotal = cut.Find("[data-testid='dash-banks-total']").QuerySelectorAll("td");
        Assert.Contains("₡365,000.00 · $730.00", banksTotal[1].TextContent);
        Assert.Contains("₡310,000.00 · $620.00", banksTotal[2].TextContent);
        // CARDS-2: "By card" — the alias, its count and spend; the "no card" bucket last with its own label; a total row.
        var cardRows = cut.FindAll("[data-testid='dash-card-row']");
        Assert.Equal(2, cardRows.Count);
        Assert.Contains("Allan's Visa", cardRows[0].TextContent);
        Assert.Contains("₡10,000.00 · $20.00", cardRows[0].QuerySelectorAll("td")[1].TextContent);
        // Only money earns a column; the kind (which explains the Bank account row next door) and the count ride under the alias.
        Assert.Equal("Dash_CardMeta[Cards_KindCredit, 1]", cardRows[0].QuerySelector("[data-testid='dash-card-meta']")!.TextContent.Trim());
        Assert.Equal("Dash_CardCount[1]", cardRows[1].QuerySelector("[data-testid='dash-card-meta']")!.TextContent.Trim()); // the "no card" bucket has no kind
        Assert.Equal(["3%", "97%"], cardRows.Select(r => r.QuerySelector("[data-testid='dash-card-share']")!.TextContent.Trim()));
        Assert.Equal("none", cardRows[1].GetAttribute("data-card"));
        Assert.Contains("Tx_NoCard", cardRows[1].TextContent);
        Assert.Contains("₡310,000.00 · $620.00", cut.Find("[data-testid='dash-cards-total']").QuerySelectorAll("td")[1].TextContent);
        Assert.Equal(2, cut.FindAll("[data-testid='dash-month'] option").Count);
    }

    [Fact]
    public async Task NoMonths_ShowsTheEmptyState()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", "[]");

        var cut = Render<Dashboard>();

        cut.WaitForElement("[data-testid='dash-empty']");
        Assert.DoesNotContain(Http.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/summary"));
    }

    [Fact]
    public async Task RateUnavailable_BlocksProjections_KeepsTheMonthHeader()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, $"/api/months/{M1}/summary", Dash(M1, rateUnavailable: true));

        var cut = Render<Dashboard>(p => p.Add(x => x.Id, Guid.Parse(M1)));

        cut.WaitForElement("[data-testid='dash-rate-unavailable']");
        Assert.Empty(cut.FindAll("[data-testid='dash-waterfall']"));
        Assert.Contains("2026", cut.Find("[data-testid='dash-title']").TextContent);
        Assert.Equal($"/months/{M1}", cut.Find("[data-testid='dash-month-link']").GetAttribute("href"));
    }

    [Fact]
    public async Task DeletedMonth_ShowsTheNotFoundMessage()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, $"/api/months/{M2}/summary", "", HttpStatusCode.NotFound);

        var cut = Render<Dashboard>();

        cut.WaitForElement("[data-testid='dash-error']");
        Assert.Contains("Month_NotFound", cut.Find("[data-testid='dash-error']").TextContent);
    }
}
