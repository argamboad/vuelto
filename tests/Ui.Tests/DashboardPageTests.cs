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
         "fixed_expenses":[{"name":"Mortgage","budget":{"crc":350000,"usd":700},"actual":{"crc":300000,"usd":600}},{"name":"Water","budget":{"crc":15000,"usd":30},"actual":{"crc":18000,"usd":36}}],
         "variable_expenses":[],
         "other_spending":[{"category_name":"Dining","actual":{"crc":10000,"usd":20}}],
         "weekly_budgeted":[{"week_number":1,"start_date":"2026-06-25","end_date":"2026-07-01","total":{"crc":0,"usd":0}},{"week_number":2,"start_date":"2026-07-02","end_date":"2026-07-08","total":{"crc":300000,"usd":600}}],
         "weekly_extraordinary":[{"week_number":1,"start_date":"2026-06-25","end_date":"2026-07-01","total":{"crc":0,"usd":0}},{"week_number":2,"start_date":"2026-07-02","end_date":"2026-07-08","total":{"crc":0,"usd":0}}],
         "current_balance":{"crc":1190000,"usd":2380},"remainder_for_debts":{"crc":1150000,"usd":2300},"pending_budgeted":{"crc":50000,"usd":100},"actual_remainder":{"crc":1140000,"usd":2280},
         "unplanned_essential_total":{"crc":10000,"usd":20},"refunds_total":{"crc":5000,"usd":10},
         "envelope_reminders":[{"name":"Marchamo","annual_target":{"crc":718000,"usd":0},"contributed_this_month":{"crc":0,"usd":0},"remaining":{"crc":718000,"usd":0},"cadence":"monthly"}],
         "bank_method_breakdown":[{"bank_id":"cccccccc-0000-0000-0000-000000000003","bank_name":"BAC","payment_method":"bank_account","budget":{"crc":365000,"usd":730},"actual":{"crc":300000,"usd":600}},{"bank_id":null,"bank_name":"","payment_method":"credit_card","budget":{"crc":0,"usd":0},"actual":{"crc":10000,"usd":20}}]}
        """;

    [Fact]
    public async Task Loads_TheNewestMonth_AndRendersEverySection()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", Months);
        Http.On(HttpMethod.Get, $"/api/months/{M2}/summary", Dash(M2));

        var cut = Render<Dashboard>();

        cut.WaitForElement("[data-testid='dash-income']");
        Assert.Single(Http.Requests, r => r.RequestUri!.AbsolutePath == $"/api/months/{M2}/summary"); // newest first
        Assert.Contains("₡1,500,000.00 · $3,000.00", cut.Find("[data-testid='dash-income']").TextContent);
        Assert.Contains("₡310,000.00", cut.Find("[data-testid='dash-expenses']").TextContent);
        Assert.Contains("₡1,140,000.00", cut.Find("[data-testid='dash-balance']").TextContent);
        Assert.Contains("500.00", cut.Find("[data-testid='dash-rate']").TextContent);

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
        var bankRows = cut.FindAll("[data-testid='dash-bank-row']");
        Assert.Contains("BAC", bankRows[0].TextContent);
        Assert.Contains("Budget_Unassigned", bankRows[1].TextContent);
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
        Assert.Empty(cut.FindAll("[data-testid='dash-income']"));
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
