using System.Net;
using Bunit;
using Vuelto.Shared.Ui.Components;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// DASH-1 UI as SKIN-5 presents it: one verdict, the pace, the four-step waterfall, the line lists, and one
/// breakdown panel. The ARITHMETIC is unchanged from the eleven-row version these tests used to cover — the
/// same figures in the same order — so what moved is where each number is read, not what it is.
/// </summary>
public class DashboardPageTests : ComponentTestBase
{
    private const string M1 = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string M2 = "aaaaaaaa-0000-0000-0000-000000000002";
    private const string Months = $$"""[{"id":"{{M2}}","year":2026,"month_number":7},{"id":"{{M1}}","year":2026,"month_number":6}]""";

    private static string Dash(string monthId, bool rateUnavailable = false, string? summary = null) => $$"""
        {"month":{"id":"{{monthId}}","year":2026,"month_number":7,"week_count":5,"week1_start_date":"2026-06-25","last_day":"2026-07-29"},
         "exchange_rate":{{(rateUnavailable ? "null" : "500")}},"rate_source":{{(rateUnavailable ? "null" : "\"cache\"")}},"rate_as_of":"2026-09-03T12:00:00+00:00","rate_unavailable":{{(rateUnavailable ? "true" : "false")}},
         "summary":{{(rateUnavailable ? "null" : summary ?? Summary)}}}
        """;

    private const string Summary = """
        {"income_primary":{"crc":1500000,"usd":3000},"income_secondary":{"crc":0,"usd":0},"income_total":{"crc":1500000,"usd":3000},
         "expenses_card":{"crc":10000,"usd":20},"expenses_account":{"crc":300000,"usd":600},"expenses_total":{"crc":310000,"usd":620},"expenses_remainder":{"crc":1190000,"usd":2380},
         "spent_budgeted":{"crc":300000,"usd":600},"spent_extraordinary":{"crc":0,"usd":0},"spent_unplanned":{"crc":10000,"usd":20},
         "fixed_expenses":[{"name":"Mortgage","budget":{"crc":350000,"usd":700},"actual":{"crc":300000,"usd":600}},{"name":"Water","budget":{"crc":15000,"usd":30},"actual":{"crc":18000,"usd":36}}],
         "variable_expenses":[],
         "other_spending":[{"category_name":"Dining","actual":{"crc":10000,"usd":20},"by_class":[{"class":"unplanned_essential","actual":{"crc":10000,"usd":20}}]},{"category_name":"Trips","actual":{"crc":4000,"usd":8},"by_class":[{"class":"budgeted","actual":{"crc":1000,"usd":2}},{"class":"extraordinary","actual":{"crc":3000,"usd":6}}]}],
         "weekly_budgeted":[{"week_number":1,"start_date":"2026-06-25","end_date":"2026-07-01","total":{"crc":0,"usd":0}},{"week_number":2,"start_date":"2026-07-02","end_date":"2026-07-08","total":{"crc":300000,"usd":600}}],
         "weekly_extraordinary":[{"week_number":1,"start_date":"2026-06-25","end_date":"2026-07-01","total":{"crc":0,"usd":0}},{"week_number":2,"start_date":"2026-07-02","end_date":"2026-07-08","total":{"crc":0,"usd":0}}],
         "current_balance":{"crc":1190000,"usd":2380},"remainder_for_debts":{"crc":1150000,"usd":2300},"pending_budgeted":{"crc":50000,"usd":100},"actual_remainder":{"crc":1140000,"usd":2280},
         "unplanned_essential_total":{"crc":10000,"usd":20},"refunds_total":{"crc":5000,"usd":10},
         "envelope_reminders":[{"name":"Marchamo","annual_target":{"crc":718000,"usd":0},"contributed_this_month":{"crc":0,"usd":0},"remaining":{"crc":718000,"usd":0},"cadence":"monthly"}],
         "bank_method_breakdown":[{"bank_id":"cccccccc-0000-0000-0000-000000000003","bank_name":"BAC","payment_method":"bank_account","actual":{"crc":300000,"usd":600}},{"bank_id":"cccccccc-0000-0000-0000-000000000004","bank_name":"Cash","payment_method":"credit_card","actual":{"crc":10000,"usd":20}}],
         "method_breakdown":[{"payment_method":"credit_card","budget":{"crc":15000,"usd":30},"actual":{"crc":10000,"usd":20}},{"payment_method":"bank_account","budget":{"crc":350000,"usd":700},"actual":{"crc":300000,"usd":600}}],
         "by_card":[{"card_id":"eeeeeeee-0000-0000-0000-000000000005","card_name":"Allan's Visa","actual":{"crc":10000,"usd":20},"count":1},{"card_id":null,"card_name":"","actual":{"crc":300000,"usd":600},"count":1}]}
        """;

    /// <summary>Jul 15 of a Jun 25 – Jul 29 month = day 21 of 35 = 60% elapsed. Spend is 21% of income.</summary>
    private static readonly DateOnly MidMonth = new(2026, 7, 15);

    private async Task<IRenderedComponent<Dashboard>> DashboardAsync(DateOnly today, string? summary = null)
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, $"/api/months/{M2}/summary", Dash(M2, summary: summary));
        var cut = Render<Dashboard>(p => p.Add(x => x.Today, today));
        cut.WaitForElement("[data-testid='dash-verdict']");
        return cut;
    }

    // ---------------------------------------------------------------- the verdict

    [Fact]
    public async Task TheVerdictIsDerived_FromForecastAndPace_NotStored()
    {
        // Spent 21% of income with 60% of the month gone, and a forecast still in the black.
        var onTrack = await DashboardAsync(MidMonth);
        Assert.Equal("Dash_Verdict_OnTrack", onTrack.Find("[data-testid='dash-verdict-state']").TextContent.Trim());
        Assert.Equal("good", onTrack.Find("[data-testid='dash-verdict']").GetAttribute("data-tone"));

        // Same spend, but only the second day of the month — ahead of pace, though nothing is over yet.
        var watch = await DashboardAsync(new DateOnly(2026, 6, 26));
        Assert.Equal("Dash_Verdict_AtRisk", watch.Find("[data-testid='dash-verdict-state']").TextContent.Trim());
        Assert.Equal("warn", watch.Find("[data-testid='dash-verdict']").GetAttribute("data-tone"));
    }

    [Fact]
    public async Task AForecastBelowZeroIsOver_WhateverThePace_AndSaysThePlanDoesNotFit()
    {
        // Still planned ₡1,300,000 against ₡1,190,000 left → forecast −₡110,000.
        var overPlan = Summary.Replace("""
            "pending_budgeted":{"crc":50000,"usd":100},"actual_remainder":{"crc":1140000,"usd":2280}
            """.Trim(), """
            "pending_budgeted":{"crc":1300000,"usd":2600},"actual_remainder":{"crc":-110000,"usd":-220}
            """.Trim());

        var cut = await DashboardAsync(MidMonth, overPlan);

        Assert.Equal("Dash_Verdict_Over", cut.Find("[data-testid='dash-verdict-state']").TextContent.Trim());
        Assert.Equal("bad", cut.Find("[data-testid='dash-verdict']").GetAttribute("data-tone"));
        Assert.Contains("₡-110,000.00", cut.Find("[data-testid='dash-forecast-primary']").TextContent);
        Assert.Contains("Dash_WfOverPlan", cut.Find("[data-testid='dash-wf-overplan']").TextContent);
        // The step carries the tone too, so the number is not the only thing that says it.
        Assert.Equal("bad", cut.Find("[data-testid='dash-wf-forecast']").Closest("[data-tone]")!.GetAttribute("data-tone"));
    }

    [Fact]
    public async Task TheStateIsWordsNotJustColour_AndTheDotIsDecorative()
    {
        var cut = await DashboardAsync(MidMonth);
        Assert.Equal("true", cut.Find("[data-testid='dash-verdict-dot']").GetAttribute("aria-hidden"));
        Assert.NotEmpty(cut.Find("[data-testid='dash-verdict-state']").TextContent.Trim());
        Assert.Contains("Dash_ForecastLeftLabel", cut.Find("[data-testid='dash-verdict-caption']").TextContent);
    }

    // ---------------------------------------------------------------- the pace

    [Fact]
    public async Task ThePaceBarDrawsFiveSegments_AndItsLegendCarriesTheMoney_NotJustAShare()
    {
        var cut = await DashboardAsync(MidMonth);

        var segments = cut.FindAll("[data-testid='dash-bar-segment']");
        // Discretionary is zero in this fixture, so four of the five are drawn.
        Assert.Equal(4, segments.Count);
        Assert.Equal("true", segments.Single(s => s.GetAttribute("data-hatched") == "true").GetAttribute("data-hatched"));

        // Owner decision: the class split stays MONEY, not a percentage of a bar.
        var legend = cut.Find("[data-testid='dash-bar-legend']").TextContent;
        Assert.Contains("Tx_Budgeted ₡300,000.00", legend);
        Assert.Contains("Tx_Unplanned ₡10,000.00", legend);
        Assert.Contains("Dash_BarPlanned ₡50,000.00", legend);

        // Committed = (spent + still planned) / income = 24%; elapsed = 60%.
        var summary = cut.Find("[data-testid='dash-pace-summary']").TextContent;
        Assert.Contains("Dash_PaceCommitted[24]", summary);
        Assert.Contains("Dash_MonthElapsed[60]", summary);
    }

    [Fact]
    public async Task TheMarkerClampsToTheEndOfTheMonth()
    {
        var cut = await DashboardAsync(new DateOnly(2026, 9, 5)); // long past the Jul 29 close
        Assert.Contains("100%", cut.Find("[data-testid='dash-bar-marker']").GetAttribute("style"));
        Assert.Contains("Dash_MonthElapsed[100]", cut.Find("[data-testid='dash-pace-summary']").TextContent);
    }

    // ---------------------------------------------------------------- the four steps

    [Fact]
    public async Task TheFourStepsAreTheSameArithmeticInTheSameOrder()
    {
        var cut = await DashboardAsync(MidMonth);

        Assert.Equal(4, cut.FindAll("[data-testid='dash-wf-step']").Count);
        Assert.Contains("₡1,500,000.00", cut.Find("[data-testid='dash-wf-income']").TextContent);
        Assert.Contains("₡310,000.00", cut.Find("[data-testid='dash-wf-spent']").TextContent);
        Assert.Contains("₡50,000.00", cut.Find("[data-testid='dash-wf-planned']").TextContent);
        Assert.Contains("₡1,140,000.00", cut.Find("[data-testid='dash-wf-forecast']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='dash-wf-overplan']"));

        // Refunds are excluded from the forecast (ADR-V007) and the step has to keep saying so.
        var forecast = cut.Find("[data-testid='dash-wf-forecast']").Closest(".wf-step")!.TextContent;
        Assert.Contains("Dash_WfRefundsExpected", forecast); // the amount expected back, named on the step it is NOT counted in
        Assert.Contains("₡5,000.00 · $10.00", forecast);
        Assert.Contains("Dash_RefundsNote", forecast);
    }

    [Fact]
    public async Task InflowsAreNamedOnTheIncomeStep_SoTheFigureDoesNotLookWrong()
    {
        // Primary 1,500,000 but total 1,550,000 → 50,000 arrived as inflows.
        var withInflow = Summary.Replace("""
            "income_total":{"crc":1500000,"usd":3000}
            """.Trim(), """
            "income_total":{"crc":1550000,"usd":3100}
            """.Trim());

        var cut = await DashboardAsync(MidMonth, withInflow);
        var step = cut.Find("[data-testid='dash-wf-income']").Closest(".wf-step")!;
        Assert.Contains("Dash_WfIncomeOther", step.TextContent);
        Assert.Contains("₡50,000.00", step.TextContent);

        // With nothing extra, the sub-line is absent rather than showing a zero.
        var plain = await DashboardAsync(MidMonth);
        Assert.DoesNotContain("Dash_WfIncomeOther", plain.Find("[data-testid='dash-wf-income']").Closest(".wf-step")!.TextContent);
    }

    // ---------------------------------------------------------------- the line lists

    [Fact]
    public async Task ALineIsJudgedInItsOwnCurrency_AndShowsHowFarOffPlanItIs()
    {
        var cut = await DashboardAsync(MidMonth);

        var rows = cut.Find("[data-testid='dash-fixed']").QuerySelectorAll("[data-testid='dash-line-row']");
        Assert.Equal(2, rows.Length);

        // Mortgage: 300,000 against 350,000 planned → ₡50,000 under.
        Assert.Null(rows[0].GetAttribute("data-over"));
        Assert.Contains("−₡50,000.00", rows[0].QuerySelector("[data-testid='dash-line-actual']")!.TextContent);

        // Water: 18,000 against 15,000 → ₡3,000 over, and the row says so without relying on colour alone.
        Assert.Equal("true", rows[1].GetAttribute("data-over"));
        Assert.Contains("+₡3,000.00", rows[1].QuerySelector("[data-testid='dash-line-actual']")!.TextContent);

        // The heading answers the same question for the whole list: 318,000 against 365,000 planned.
        Assert.Contains("Dash_LinesUnderPlan", cut.Find("[data-testid='dash-fixed-summary']").TextContent);
        Assert.Equal("good", cut.Find("[data-testid='dash-fixed-summary']").GetAttribute("data-tone"));
    }

    [Fact]
    public async Task AnEmptyListSaysSo_RatherThanRenderingAnEmptyTable()
    {
        var cut = await DashboardAsync(MidMonth);
        Assert.Contains("Dash_NoLines", cut.Find("[data-testid='dash-variable']").TextContent);
        Assert.Empty(cut.Find("[data-testid='dash-variable']").QuerySelectorAll("[data-testid='dash-line-row']"));
    }

    // ---------------------------------------------------------------- the breakdown panel

    [Fact]
    public async Task OnePanelReplacesThreeCards_AndOpensOnTheWeeks()
    {
        var cut = await DashboardAsync(MidMonth);

        Assert.Equal(2, cut.FindAll("[data-testid='dash-week-row']").Count);
        Assert.Empty(cut.FindAll("[data-testid='dash-bank-row']"));
        Assert.Empty(cut.FindAll("[data-testid='dash-card-row']"));

        var weekTotal = cut.Find("[data-testid='dash-week-total']").QuerySelectorAll("td");
        Assert.Contains("₡300,000.00", weekTotal[1].TextContent);
    }

    [Fact]
    public async Task TheSwitchChangesTheCut_WithoutRefetching()
    {
        var cut = await DashboardAsync(MidMonth);
        var before = Http.Requests.Count;

        cut.Find("[data-testid='dash-breakdown-switch-bank']").Change(true);
        // Owner, 2026-09-14: a plan names no bank, so the bank cut is actuals only (three cells: bank, method, actual)
        // under a Card / Bank account summary that carries the plan-vs-spent comparison.
        var methodRows = cut.FindAll("[data-testid='dash-method-row']");
        Assert.Equal(2, methodRows.Count);
        Assert.Contains("Tx_CreditCard", methodRows[0].TextContent);
        Assert.Contains("₡15,000.00", methodRows[0].TextContent);
        Assert.Contains("₡350,000.00", methodRows[1].TextContent);
        var bankRows = cut.FindAll("[data-testid='dash-bank-row']");
        Assert.Equal(2, bankRows.Count);
        Assert.Contains("BAC", bankRows[0].TextContent);
        Assert.Equal(3, bankRows[0].QuerySelectorAll("td").Length);
        Assert.Contains("₡310,000.00", cut.Find("[data-testid='dash-banks-total']").TextContent);

        cut.Find("[data-testid='dash-breakdown-switch-card']").Change(true);
        var cardRows = cut.FindAll("[data-testid='dash-card-row']");
        Assert.Equal(2, cardRows.Count);
        Assert.Equal("Dash_CardCount[1]", cardRows[0].QuerySelector("[data-testid='dash-card-meta']")!.TextContent.Trim());
        Assert.Equal(["3%", "97%"], cardRows.Select(r => r.QuerySelector("[data-testid='dash-card-share']")!.TextContent.Trim()));

        // Build notes: the switch is local UI state only.
        Assert.Equal(before, Http.Requests.Count);
    }

    [Fact]
    public async Task UnbudgetedIsTheFourthCut_BecauseThatIsAlsoWhereMoneyWent()
    {
        // Owner decision, 2026-09-11: the old Other-spending card becomes an option here.
        var cut = await DashboardAsync(MidMonth);
        cut.Find("[data-testid='dash-breakdown-switch-other']").Change(true);
        // Owner decision, 2026-09-14: the rows group by class with a subtotal each, so "how much of this was
        // unplanned" is read off a heading, not added up by eye. Discretionary first, then Unplanned, then the
        // catalog smell — money classed Budgeted in a category no line covers — only when it occurs.
        var groups = cut.FindAll("[data-testid='dash-other-group']");
        Assert.Equal(["extraordinary", "unplanned_essential", "budgeted"], groups.Select(g => g.GetAttribute("data-class")).ToList());
        Assert.Contains("Tx_Extraordinary", groups[0].TextContent);
        Assert.Contains("₡3,000.00", groups[0].TextContent);
        Assert.Contains("Tx_Unplanned", groups[1].TextContent);
        Assert.Contains("₡10,000.00", groups[1].TextContent);
        Assert.Contains("Dash_BudgetedNoLine", groups[2].TextContent);
        Assert.Contains("₡1,000.00", groups[2].TextContent);

        // A category whose money came in two classes appears once per class, with that class's amount.
        var rows = cut.FindAll("[data-testid='dash-other-row']");
        Assert.Equal(["Trips", "Dining", "Trips"], rows.Select(r => r.QuerySelector("td")!.TextContent.Trim()).ToList());
        Assert.Contains("₡3,000.00", rows[0].TextContent);
        Assert.Contains("₡1,000.00", rows[2].TextContent);
        Assert.Empty(cut.FindAll("[data-testid='dash-other-class']")); // no chips: the heading says the class

        Assert.Contains("₡14,000.00", cut.Find("[data-testid='dash-other-total']").TextContent); // Dining 10,000 + Trips 4,000
    }

    // ---------------------------------------------------------------- envelopes

    [Fact]
    public async Task EnvelopesAreAStrip_ShownOnlyWhenABucketIsDue()
    {
        var cut = await DashboardAsync(MidMonth);
        Assert.Contains("Marchamo", cut.Find("[data-testid='dash-envelopes']").TextContent);

        // Nothing due → the section is absent entirely, which is one fewer thing to read that month.
        var none = Summary.Replace("""
            "envelope_reminders":[{"name":"Marchamo","annual_target":{"crc":718000,"usd":0},"contributed_this_month":{"crc":0,"usd":0},"remaining":{"crc":718000,"usd":0},"cadence":"monthly"}]
            """.Trim(), "\"envelope_reminders\":[]");
        var quiet = await DashboardAsync(MidMonth, none);
        Assert.Empty(quiet.FindAll("[data-testid='dash-envelopes']"));
    }

    // ---------------------------------------------------------------- currency

    [Fact]
    public async Task ShowIn_DrivesEveryConvertedPair_ButABudgetLineKeepsItsOwnCurrency()
    {
        var cut = await DashboardAsync(MidMonth);

        // Default is "both": the second currency STACKS rather than doubling the line.
        Assert.Contains("₡1,500,000.00", cut.Find("[data-testid='dash-wf-income-primary']").TextContent);
        Assert.Contains("$3,000.00", cut.Find("[data-testid='dash-wf-income-secondary']").TextContent);

        cut.Find("[data-testid='dash-cur-usd']").Click();
        cut.WaitForAssertion(() => Assert.Contains("$3,000.00", cut.Find("[data-testid='dash-wf-income-primary']").TextContent));
        Assert.DoesNotContain("₡", cut.Find("[data-testid='dash-wf-income-primary']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='dash-wf-income-secondary']"));

        // The legend follows the same preference (owner decision), so it reads in dollars too.
        Assert.Contains("Tx_Budgeted $600.00", cut.Find("[data-testid='dash-bar-legend']").TextContent);
    }

    [Fact]
    public async Task ShowIn_FollowsTheAccount_AndSavesThere()
    {
        Http.On(HttpMethod.Get, "/api/display-settings", """{"display_currency":"USD"}""");
        Http.On(HttpMethod.Put, "/api/display-settings", """{"display_currency":"CRC"}""");

        var cut = await DashboardAsync(MidMonth);
        cut.WaitForAssertion(() => Assert.DoesNotContain("₡", cut.Find("[data-testid='dash-wf-income-primary']").TextContent));

        cut.Find("[data-testid='dash-cur-crc']").Click();
        cut.WaitForAssertion(() => Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath == "/api/display-settings"));
    }

    // ---------------------------------------------------------------- states

    [Fact]
    public async Task NoRate_BlocksTheProjections_RatherThanGuessing()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, $"/api/months/{M2}/summary", Dash(M2, rateUnavailable: true));

        var cut = Render<Dashboard>(p => p.Add(x => x.Today, MidMonth));

        cut.WaitForElement("[data-testid='dash-rate-unavailable']");
        Assert.Empty(cut.FindAll("[data-testid='dash-verdict']"));
        Assert.Empty(cut.FindAll("[data-testid='dash-wf']"));
    }

    [Fact]
    public async Task NoMonths_ShowsTheEmptyState()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", "[]");

        var cut = Render<Dashboard>();

        cut.WaitForElement("[data-testid='dash-empty']");
        Assert.NotNull(cut.Find("[data-testid='dash-new-tx']"));
    }

    [Fact]
    public async Task TheNewestMonthLoadsFirst_AndItsHeadNamesTheWindow()
    {
        var cut = await DashboardAsync(MidMonth);

        Assert.Contains("2026", cut.Find("[data-testid='dash-title']").TextContent);
        Assert.Contains("Dash_DayOfMonth[21, 35]", cut.Find(".dash-subtitle").TextContent);
        Assert.Equal(M2, cut.Find("[data-testid='dash-month'] option[selected]").GetAttribute("value"));
    }
}
