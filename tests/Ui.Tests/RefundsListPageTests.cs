using Bunit;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// #208: the refunds page — every refund across months, filtered by status / payee / dates on the API, grouped on the
/// page with each group's received and pending, several pending ones marked received together, and the status and
/// grouping remembered on the device.
/// </summary>
public class RefundsListPageTests : ComponentTestBase
{
    private const string June = "aaaaaaaa-0000-0000-0000-000000000006";
    private const string July = "aaaaaaaa-0000-0000-0000-000000000007";
    private const string R1 = "eeeeeeee-0000-0000-0000-000000000001";
    private const string R2 = "eeeeeeee-0000-0000-0000-000000000002";
    private const string R3 = "eeeeeeee-0000-0000-0000-000000000003";

    private const string ThreeRefunds = $$"""
        {"totals":{"pending":{"crc":25000,"usd":50},"received":{"crc":6170,"usd":12.34},"expected":{"crc":31170,"usd":62.34} },
         "truncated":false,
         "refunds":[
          {"id":"{{R1}}","month_id":"{{June}}","payee":"Hospital","transaction_date":"2026-06-05","amount_crc":15000,"amount_usd":30,"status":"pending","month_year":2026,"month_number":6,"pending_days":90},
          {"id":"{{R2}}","month_id":"{{June}}","payee":"hospital","transaction_date":"2026-06-20","amount_crc":6170,"amount_usd":12.34,"status":"received","received_date":"2026-07-01","month_year":2026,"month_number":6},
          {"id":"{{R3}}","month_id":"{{July}}","payee":"Farmacia Fischel","transaction_date":"2026-07-10","amount_crc":10000,"amount_usd":20,"status":"pending","month_year":2026,"month_number":7,"pending_days":55}]}
        """;

    private async Task<IRenderedComponent<Refunds>> RenderPageAsync(string json = ThreeRefunds)
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/refunds", json);
        var cut = Render<Refunds>();
        cut.WaitForElement("[data-testid='refunds-totals']");
        return cut;
    }

    private IEnumerable<string> RefundQueries() =>
        Http.Requests.Where(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/refunds").Select(r => r.RequestUri!.Query);

    [Fact]
    public async Task OpensOnPending_OldestFirst_WithTotals_TheMonth_AndHowLongEachHasBeenOut()
    {
        var cut = await RenderPageAsync();

        Assert.Equal("?status=pending", RefundQueries().First()); // what still has to come in, by default
        Assert.Contains("6,170.00", cut.Find("[data-testid='refunds-received']").TextContent);
        Assert.Contains("25,000.00", cut.Find("[data-testid='refunds-pending']").TextContent);
        Assert.Contains("31,170.00", cut.Find("[data-testid='refunds-expected']").TextContent);
        var rows = cut.FindAll("[data-testid='refunds-row']");
        Assert.Equal([R1, R2, R3], rows.Select(r => r.GetAttribute("data-id")));
        Assert.Equal(["90", "55"], cut.FindAll("[data-testid='refunds-age']").Select(a => a.GetAttribute("data-days")));
        Assert.Contains("Refunds_PendingFor[90]", rows[0].TextContent);
        Assert.Equal($"/months/{July}", rows[2].QuerySelector("a")!.GetAttribute("href"));
        Assert.Equal(2, cut.FindAll("[data-testid='refunds-select']").Count); // only pending ones can be selected
        Assert.Equal(3, cut.FindAll("[data-testid='refunds-status-chip']").Count);
        Assert.Equal(3, cut.FindAll("[data-testid='refunds-amount']").Count);
        Assert.Empty(cut.FindAll("[data-testid='refunds-group-head']"));
    }

    [Fact]
    public async Task Filters_GoToTheApi_AndTheStatusIsRemembered()
    {
        var cut = await RenderPageAsync();

        cut.Find("[data-testid='refunds-status-option'][data-value='all']").Change(true);
        cut.WaitForAssertion(() => Assert.Equal(2, RefundQueries().Count()));
        Assert.Equal("", RefundQueries().Last()); // both statuses: no filter
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "appUi.setPref" && Equals(i.Arguments[0], "refunds.status") && Equals(i.Arguments[1], "all"));

        cut.Find("[data-testid='refunds-payee']").Change(" Fischel ");
        cut.WaitForAssertion(() => Assert.Equal("?payee=Fischel", RefundQueries().Last()));

        cut.Find("[data-testid='refunds-from']").Change("2026-07-01");
        cut.Find("[data-testid='refunds-to']").Change("2026-07-31");
        cut.WaitForAssertion(() => Assert.Equal("?payee=Fischel&from=2026-07-01&to=2026-07-31", RefundQueries().Last()));
    }

    [Fact]
    public async Task RememberedChoices_AreWhereThePageOpens()
    {
        JSInterop.Setup<string?>("appUi.getPref", "refunds.status").SetResult("received");
        JSInterop.Setup<string?>("appUi.getPref", "refunds.group").SetResult("month");

        var cut = await RenderPageAsync();

        Assert.Equal("?status=received", RefundQueries().First());
        Assert.Equal(["2026-06", "2026-07"], cut.FindAll("[data-testid='refunds-group-head']").Select(g => g.GetAttribute("data-key")));
    }

    [Fact]
    public async Task GroupedByPayee_OneGroupPerPayee_IgnoringCase_WithItsReceivedAndPending()
    {
        var cut = await RenderPageAsync();

        cut.Find("[data-testid='refunds-group-option'][data-value='payee']").Change(true);

        var groups = cut.FindAll("[data-testid='refunds-group-head']");
        Assert.Equal(["Farmacia Fischel", "Hospital"], groups.Select(g => g.GetAttribute("data-key")));
        Assert.Contains("₡6,170.00", groups[1].TextContent);  // Hospital received
        Assert.Contains("₡15,000.00", groups[1].TextContent); // Hospital pending
        Assert.Contains(JSInterop.Invocations, i => i.Identifier == "appUi.setPref" && Equals(i.Arguments[0], "refunds.group") && Equals(i.Arguments[1], "payee"));

        cut.Find("[data-testid='refunds-group-option'][data-value='status']").Change(true);
        Assert.Equal(["pending", "received"], cut.FindAll("[data-testid='refunds-group-head']").Select(g => g.GetAttribute("data-key")));
        Assert.Single(RefundQueries()); // grouping is the page's, no refetch
    }

    [Fact]
    public async Task SeveralPending_MarkedReceivedTogether_EachThroughItsOwnFlip_OnTheDepositsDay()
    {
        var cut = await RenderPageAsync();
        Http.On(HttpMethod.Put, $"/api/refunds/{R1}", "{}");
        Http.On(HttpMethod.Put, $"/api/refunds/{R3}", "{}");

        for (var i = 0; i < 2; i++) cut.FindAll("[data-testid='refunds-select']")[i].Change(true); // re-found: each tick re-renders
        Assert.Contains("Refunds_Selected[2]", cut.Find("[data-testid='refunds-bulk']").TextContent);
        cut.Find("[data-testid='refunds-bulk-date']").Change("2026-09-01");
        cut.Find("[data-testid='refunds-bulk-receive']").Click();

        cut.WaitForAssertion(() => Assert.Contains("Refunds_MarkedReceived[2]", cut.Find("[data-testid='refunds-notice']").TextContent));
        var puts = Http.Requests.Where(r => r.Method == HttpMethod.Put).ToList();
        Assert.Equal([$"/api/refunds/{R1}", $"/api/refunds/{R3}"], puts.Select(p => p.RequestUri!.AbsolutePath));
        foreach (var put in puts)
        {
            var body = await put.Content!.ReadAsStringAsync();
            Assert.Contains("\"status\":\"received\"", body);
            Assert.Contains("\"received_date\":\"2026-09-01\"", body);
        }
        Assert.Equal(2, RefundQueries().Count()); // reloaded
        Assert.Empty(cut.FindAll("[data-testid='refunds-bulk']"));
    }

    [Fact]
    public async Task OneThatFails_IsNamed_AndTheOthersStillLand()
    {
        var cut = await RenderPageAsync();
        Http.On(HttpMethod.Put, $"/api/refunds/{R1}", "{}");
        Http.On(HttpMethod.Put, $"/api/refunds/{R3}", """{"error":"invalid_request","message":"received_date cannot be before the transaction date"}""", System.Net.HttpStatusCode.BadRequest);

        for (var i = 0; i < 2; i++) cut.FindAll("[data-testid='refunds-select']")[i].Change(true); // re-found: each tick re-renders
        cut.Find("[data-testid='refunds-bulk-receive']").Click();

        cut.WaitForAssertion(() => Assert.Contains("Refunds_MarkedSome[1, Farmacia Fischel]", cut.Find("[data-testid='refunds-notice']").TextContent));
    }

    [Fact]
    public async Task NothingMatching_SaysSo_AndAFailedLoad_SaysThat()
    {
        var cut = await RenderPageAsync("""{"totals":{"pending":{"crc":0,"usd":0},"received":{"crc":0,"usd":0},"expected":{"crc":0,"usd":0} },"refunds":[],"truncated":true}""");
        Assert.NotNull(cut.Find("[data-testid='refunds-empty']"));
        Assert.NotNull(cut.Find("[data-testid='refunds-truncated']"));

        Http.On(HttpMethod.Get, "/api/refunds", """{"error":"invalid_request","message":"from must be on or before to"}""", System.Net.HttpStatusCode.BadRequest);
        cut.Find("[data-testid='refunds-payee']").Change("x");
        cut.WaitForAssertion(() => Assert.Contains("from must be on or before to", cut.Find("[data-testid='refunds-error']").TextContent));
    }
}
