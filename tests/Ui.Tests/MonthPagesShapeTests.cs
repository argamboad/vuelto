using System.Net;
using Bunit;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// SKIN-8: the Months list is a card grid fed by each month's summary, and the month page is a ledger with
/// one stacked amount column, class chips, a merged "paid with" column, phone sub-lines and status pills.
/// The arithmetic and the endpoints are untouched — only the shape is under test here.
/// </summary>
public class MonthPagesShapeTests : ComponentTestBase
{
    private const string CurrentId = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string ClosedId = "aaaaaaaa-0000-0000-0000-000000000002";
    private const string UpcomingId = "aaaaaaaa-0000-0000-0000-000000000003";
    private const string RefundId = "eeeeeeee-0000-0000-0000-000000000005";

    private static string Iso(DateTime d) => d.ToString("yyyy-MM-dd");

    /// <summary>Three months around today: one whose window holds today, one that ended, one that has not started.</summary>
    private void StubThreeMonths()
    {
        var today = DateTime.Today;
        Http.On(HttpMethod.Get, "/api/months", $$"""
            [{"id":"{{UpcomingId}}","year":2026,"month_number":11,"week_count":4,"week1_start_date":"{{Iso(today.AddDays(40))}}"},
             {"id":"{{CurrentId}}","year":2026,"month_number":10,"week_count":4,"week1_start_date":"{{Iso(today.AddDays(-3))}}"},
             {"id":"{{ClosedId}}","year":2026,"month_number":9,"week_count":5,"week1_start_date":"{{Iso(today.AddDays(-70))}}"}]
            """);
    }

    private static string Summary(decimal income, decimal spent, decimal planned, decimal forecast) => $$"""
        {"month":{"id":"x","year":2026,"month_number":10,"week_count":4,"week1_start_date":"2026-09-24","last_day":"2026-10-21"},
         "exchange_rate":500,"rate_unavailable":false,
         "summary":{"income_total":{"crc":{{income}},"usd":{{income / 500}}},"expenses_total":{"crc":{{spent}},"usd":{{spent / 500}}},
                    "pending_budgeted":{"crc":{{planned}},"usd":{{planned / 500}}},
                    "actual_remainder":{"crc":{{forecast}},"usd":{{forecast / 500}} }
                   }
        }
        """;

    // ---------------------------------------------------------------- Months

    [Fact]
    public async Task Months_RendersOneLinkCardPerMonth_WithTheChipTodayDecides()
    {
        await SignInAsync();
        StubThreeMonths();

        var cut = Render<Months>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("a[data-testid='month-row']").Count));

        var cards = cut.FindAll("a[data-testid='month-row']");
        Assert.Equal($"/months/{UpcomingId}", cards[0].GetAttribute("href"));
        Assert.Equal("upcoming", cards[0].QuerySelector("[data-testid='month-card-chip']")!.GetAttribute("data-state"));
        Assert.Equal("current", cards[1].QuerySelector("[data-testid='month-card-chip']")!.GetAttribute("data-state"));
        Assert.Equal("closed", cards[2].QuerySelector("[data-testid='month-card-chip']")!.GetAttribute("data-state"));
        Assert.Contains("Months_ChipCurrent", cards[1].QuerySelector("[data-testid='month-card-chip']")!.TextContent);
        Assert.Contains("Months_Weeks[5]", cards[2].QuerySelector("[data-testid='month-card-window']")!.TextContent);
        Assert.Contains("October 2026", cards[1].QuerySelector("[data-testid='month-card-title']")!.TextContent);
        Assert.Empty(cut.FindAll("[data-testid='month-row'] a")); // one tap target, no nested link
    }

    [Fact]
    public async Task Months_FillsTheBarAndTheFigures_FromEachMonthsSummary_ClosedMonthsShowASolidBar()
    {
        await SignInAsync();
        StubThreeMonths();
        Http.On(HttpMethod.Get, $"/api/months/{CurrentId}/summary", Summary(income: 2_000_000, spent: 1_485_800, planned: 297_000, forecast: 217_200));
        Http.On(HttpMethod.Get, $"/api/months/{ClosedId}/summary", Summary(income: 2_680_000, spent: 2_731_500, planned: 120_000, forecast: -171_500));
        Http.On(HttpMethod.Get, $"/api/months/{UpcomingId}/summary", Summary(income: 2_000_000, spent: 0, planned: 1_600_000, forecast: 400_000));

        var cut = Render<Months>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid='month-card-result']").Count));

        var current = cut.FindAll("a[data-testid='month-row']")[1];
        Assert.Contains("Months_Spent[₡1,485,800.00]", current.QuerySelector("[data-testid='month-card-spent']")!.TextContent);
        var result = current.QuerySelector("[data-testid='month-card-result']")!;
        Assert.Equal("+₡217,200.00", result.TextContent.Trim());
        Assert.Equal("good", result.GetAttribute("data-tone"));
        Assert.Equal(2, current.QuerySelectorAll("[data-testid='month-card-bar-segment']").Length); // spent, then still planned (hatched)
        Assert.Equal("true", current.QuerySelectorAll("[data-testid='month-card-bar-segment']")[1].GetAttribute("data-hatched"));
        Assert.Empty(current.QuerySelectorAll("[data-testid='month-card-bar-legend']")); // the same numbers are in the text
        var aria = current.GetAttribute("aria-label")!;
        Assert.Contains("October 2026", aria);
        Assert.Contains("Months_ChipCurrent", aria);
        Assert.Contains("Months_Spent[₡1,485,800.00]", aria);
        Assert.Contains("Months_ResultLeft[₡217,200.00]", aria);

        // Closed: the planned remainder is zero by definition, so the bar is solid and the result is what was actually left.
        var closed = cut.FindAll("a[data-testid='month-row']")[2];
        Assert.Single(closed.QuerySelectorAll("[data-testid='month-card-bar-segment']"));
        var closedResult = closed.QuerySelector("[data-testid='month-card-result']")!;
        Assert.Equal("−₡51,500.00", closedResult.TextContent.Trim()); // 2,680,000 − 2,731,500, not the forecast
        Assert.Equal("bad", closedResult.GetAttribute("data-tone"));
        Assert.Contains("Months_ResultOver[₡51,500.00]", closed.GetAttribute("aria-label"));
    }

    [Fact]
    public async Task Months_WhenAMonthsFiguresCannotBeComputed_TheCardStillRenders_WithoutThem()
    {
        await SignInAsync();
        StubThreeMonths();
        Http.On(HttpMethod.Get, $"/api/months/{CurrentId}/summary", """{"month":{"id":"x","year":2026,"month_number":10,"week_count":4,"week1_start_date":"2026-09-24","last_day":"2026-10-21"},"exchange_rate":null,"rate_unavailable":true,"summary":null}""");
        // the other two summaries are not stubbed at all → 404s; a card never depends on its figures to exist

        var cut = Render<Months>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid='month-card-nofigures']").Count));
        Assert.Equal(3, cut.FindAll("a[data-testid='month-row']").Count);
        Assert.Empty(cut.FindAll("[data-testid='month-card-result']"));
    }

    [Fact]
    public async Task Months_FollowsTheShowInPreference()
    {
        await SignInAsync();
        StubThreeMonths();
        Http.On(HttpMethod.Get, "/api/display-settings", """{"display_currency":"USD"}""");
        Http.On(HttpMethod.Get, $"/api/months/{CurrentId}/summary", Summary(income: 2_000_000, spent: 1_485_800, planned: 297_000, forecast: 217_200));

        var cut = Render<Months>();
        cut.WaitForAssertion(() => Assert.Contains("Months_Spent[$2,971.60]", cut.Find("[data-testid='month-card-spent']").TextContent));
        Assert.Equal("+$434.40", cut.Find("[data-testid='month-card-result']").TextContent.Trim());
    }

    // ---------------------------------------------------------------- Month detail

    private void StubMonth(string transactions = "[]", string refunds = "[]")
    {
        Http.On(HttpMethod.Get, $"/api/months/{CurrentId}", $$"""{"id":"{{CurrentId}}","year":2026,"month_number":7,"week_count":2,"week1_start_date":"2026-06-25","primary_income_amount":3750,"primary_income_currency":"USD","secondary_income_amount":312500,"secondary_income_currency":"CRC","weeks":[{"week_number":1,"start_date":"2026-06-25","end_date":"2026-07-01"},{"week_number":2,"start_date":"2026-07-02","end_date":"2026-07-08"}]}""");
        Http.On(HttpMethod.Get, $"/api/months/{CurrentId}/transactions", transactions);
        Http.On(HttpMethod.Get, $"/api/months/{CurrentId}/refunds", refunds);
    }

    private const string TwoRows = """
        [{"id":"dddddddd-0000-0000-0000-000000000001","payee":"Uber","transaction_date":"2026-07-06","category_name":"Transport","bank_name":"BAC","card_name":"Casa VISA","payment_method":"credit_card","transaction_type":"extraordinary","amount_crc":6750,"amount_usd":12.5,"source":"manual"},
         {"id":"dddddddd-0000-0000-0000-000000000002","payee":"AutoMercado","transaction_date":"2026-06-28","category_name":"Groceries","bank_name":"Cash","payment_method":"credit_card","transaction_type":"budgeted","amount_crc":50000,"amount_usd":100,"source":"manual"}]
        """;

    [Fact]
    public async Task MonthDetail_Header_HasTheAllMonthsLink_WeekChips_AndAOneRowIncome()
    {
        await SignInAsync();
        StubMonth();

        var cut = Render<MonthDetail>(p => p.Add(x => x.Id, Guid.Parse(CurrentId)));
        cut.WaitForElement("[data-testid='month-title']");

        Assert.Equal("/months", cut.Find("[data-testid='month-back']").GetAttribute("href"));
        var chips = cut.Find("[data-testid='month-weeks']").Children;
        Assert.Equal(2, chips.Length);
        Assert.Equal("Month_WeekChip[1, Jun 25]", chips[0].TextContent.Trim()); // "W1 · Jun 25" — the start day is the scanning cue
        Assert.Contains("Jul 1", chips[0].GetAttribute("title")); // the full window is one hover away
        Assert.Contains("Month_SaveIncome", cut.Find("[data-testid='inc-save']").TextContent);
        Assert.Equal("3750", cut.Find("[data-testid='inc-primary']").GetAttribute("value"));
        Assert.Equal($"/dashboard/{CurrentId}", cut.Find("[data-testid='month-dashboard-link']").GetAttribute("href"));
    }

    [Fact]
    public async Task MonthDetail_LedgerRow_StacksTheAmount_ChipsTheClass_MergesPaidWith_AndCarriesAPhoneSubLine()
    {
        await SignInAsync();
        StubMonth(TwoRows);

        var cut = Render<MonthDetail>(p => p.Add(x => x.Id, Guid.Parse(CurrentId)));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='month-tx-row']").Count));
        var uber = cut.FindAll("[data-testid='month-tx-row']")[0];

        // One amount cell: the active currency on top, the other muted beneath — never ₡ and $ as two columns.
        Assert.Equal("₡6,750.00", uber.QuerySelector("[data-testid='month-tx-amount-primary']")!.TextContent);
        Assert.Equal("$12.50", uber.QuerySelector("[data-testid='month-tx-amount-secondary']")!.TextContent);
        Assert.DoesNotContain(cut.FindAll("th"), th => th.TextContent.Trim() is "₡" or "$");

        // The class is the chip, keyed off the class itself.
        Assert.Equal("extraordinary", uber.QuerySelector("[data-testid='month-tx-class']")!.GetAttribute("data-class"));

        // "Paid with" is the bank, and the card's alias beneath it when there is one (CARDS-1: the alias, never the number).
        var paid = uber.QuerySelector("[data-testid='month-tx-paid']")!;
        Assert.Contains("BAC", paid.TextContent);
        Assert.Equal("Casa VISA", paid.QuerySelector("[data-testid='month-tx-card']")!.TextContent.Trim());
        Assert.Null(cut.FindAll("[data-testid='month-tx-row']")[1].QuerySelector("[data-testid='month-tx-card']"));

        // Below md the category and paid-with columns hide; the payee cell's sub-line repeats them so nothing is lost.
        Assert.Equal("Transport · BAC · Casa VISA", uber.QuerySelector("[data-testid='month-tx-sub']")!.TextContent.Trim());
        Assert.Equal("Groceries · Cash", cut.FindAll("[data-testid='month-tx-row']")[1].QuerySelector("[data-testid='month-tx-sub']")!.TextContent.Trim());
        Assert.Equal("Uber", uber.QuerySelector("[data-testid='month-tx-payee']")!.TextContent.Trim()); // the name alone; the sub-line is a sibling
        Assert.Equal("Jul 6", uber.QuerySelector("[data-testid='month-tx-date-short']")!.TextContent.Trim());
        Assert.Contains("Month_PaidWith", cut.Find("[data-testid='month-tx-sort-bank']").TextContent);
    }

    [Fact]
    public async Task MonthDetail_ShowsTheCountAsAPill_AndFoldsTheExtraFiltersBehindAToggle()
    {
        await SignInAsync();
        StubMonth(TwoRows);

        var cut = Render<MonthDetail>(p => p.Add(x => x.Id, Guid.Parse(CurrentId)));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='month-tx-row']").Count));

        Assert.Equal("2", cut.Find("[data-testid='month-tx-total']").TextContent.Trim());
        Assert.Equal("Month_SearchPayee", cut.Find("[data-testid='month-tx-filter-payee']").GetAttribute("placeholder"));
        Assert.Contains("Month_FilterAllCategories", cut.Find("[data-testid='month-tx-filter-category']").TextContent);

        var more = cut.Find("[data-testid='month-tx-filters-more']");
        Assert.Equal("false", more.GetAttribute("data-open"));
        var toggle = cut.Find("[data-testid='month-tx-filters-toggle']");
        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));
        toggle.Click();
        Assert.Equal("true", cut.Find("[data-testid='month-tx-filters-more']").GetAttribute("data-open"));
        Assert.Equal("true", cut.Find("[data-testid='month-tx-filters-toggle']").GetAttribute("aria-expanded"));
    }

    [Fact]
    public async Task MonthDetail_FollowsTheShowInPreference()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/display-settings", """{"display_currency":"USD"}""");
        StubMonth(TwoRows);

        var cut = Render<MonthDetail>(p => p.Add(x => x.Id, Guid.Parse(CurrentId)));
        cut.WaitForAssertion(() => Assert.Equal("$12.50", cut.Find("[data-testid='month-tx-amount-primary']").TextContent));
        Assert.Empty(cut.FindAll("[data-testid='month-tx-amount-secondary']")); // one side asked for, one side shown
    }

    [Fact]
    public async Task MonthDetail_RefundRow_IsAStatusPill_AndItsButtonNamesTheMerchant()
    {
        await SignInAsync();
        StubMonth(refunds: $$"""[{"id":"{{RefundId}}","month_id":"{{CurrentId}}","transaction_id":"dddddddd-0000-0000-0000-000000000004","payee":"Hospital","transaction_date":"2026-06-27","percentage":30,"amount_crc":15000,"amount_usd":30,"status":"pending","inflow_transaction_id":null}]""");

        var cut = Render<MonthDetail>(p => p.Add(x => x.Id, Guid.Parse(CurrentId)));
        cut.WaitForElement("[data-testid='month-refund-row']");

        var status = cut.Find("[data-testid='refund-status']");
        Assert.Equal("warn", status.GetAttribute("data-tone")); // pending needs attention — amber, never red
        Assert.Contains("Refund_Pending", status.TextContent);
        Assert.Equal("₡15,000.00", cut.Find("[data-testid='refund-amount-primary']").TextContent);
        Assert.Equal("Refund_MarkReceivedFor[Hospital]", cut.Find("[data-testid='refund-toggle']").GetAttribute("aria-label"));

        StubMonth(refunds: $$"""[{"id":"{{RefundId}}","month_id":"{{CurrentId}}","transaction_id":"dddddddd-0000-0000-0000-000000000004","payee":"Hospital","transaction_date":"2026-06-27","percentage":30,"amount_crc":15000,"amount_usd":30,"status":"received","inflow_transaction_id":"dddddddd-0000-0000-0000-000000000009","received_date":"2026-07-03","inflow_month_id":"{{CurrentId}}"}]""");
        var received = Render<MonthDetail>(p => p.Add(x => x.Id, Guid.Parse(CurrentId)));
        received.WaitForElement("[data-testid='month-refund-row']");
        Assert.Equal("good", received.Find("[data-testid='refund-status']").GetAttribute("data-tone"));
        Assert.Equal("Refund_MarkPendingFor[Hospital]", received.Find("[data-testid='refund-toggle']").GetAttribute("aria-label"));
    }
}
