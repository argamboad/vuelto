using System.Net;
using Bunit;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>LEDGER-3 pages: the refund fields appear only for an unplanned essential and ride the payload; the month page lists refunds and flips their status.</summary>
public class RefundPagesTests : ComponentTestBase
{
    private const string MonthId = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string CatId = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string BankId = "cccccccc-0000-0000-0000-000000000003";
    private const string RefundId = "eeeeeeee-0000-0000-0000-000000000005";

    private void StubCatalogs()
    {
        Http.On(HttpMethod.Get, "/api/categories", $$"""[{"id":"{{CatId}}","name":"Health","is_active":true}]""");
        Http.On(HttpMethod.Get, "/api/banks", $$"""[{"id":"{{BankId}}","name":"Cash","is_active":true}]""");
        Http.On(HttpMethod.Get, "/api/envelopes", "[]");
        Http.On(HttpMethod.Get, "/api/exchange-rate", """{"rate":500,"source":"live","as_of":"2026-09-03T12:00:00+00:00"}""");
        Http.On(HttpMethod.Get, "/api/months/resolve", """{"month_id":null,"year":2026,"month_number":6,"is_new":true}""");
    }

    [Fact]
    public async Task RefundFields_AppearOnlyForUnplannedEssential_AndRideThePayload()
    {
        await SignInAsync();
        StubCatalogs();
        Http.On(HttpMethod.Post, "/api/transactions", $$"""{"id":"dddddddd-0000-0000-0000-000000000004","month_id":"{{MonthId}}","payee":"Hospital","bank_id":"{{BankId}}","payment_method":"credit_card","original_amount":50000,"currency":"CRC","transaction_date":"2026-06-05","category_id":"{{CatId}}","exchange_rate_used":500,"transaction_type":"unplanned_essential","source":"manual","envelope_id":null,"refund_expected":true,"refund_percentage":30}""", HttpStatusCode.Created);

        var cut = Render<TransactionForm>();
        cut.WaitForElement("[data-testid='tx-save']");
        Assert.Empty(cut.FindAll("[data-testid='tx-refund-expected']")); // budgeted by default: no refund fields

        cut.Find("[data-testid='tx-type']").Change("unplanned_essential");
        cut.Find("[data-testid='tx-refund-expected']").Change(true);
        cut.Find("[data-testid='tx-payee']").Input("Hospital");
        cut.Find("[data-testid='tx-amount']").Change("50000");
        cut.Find("[data-testid='tx-refund-pct']").Change("30");
        Assert.Contains("Tx_RefundPreview[15,000.00 CRC]", cut.Find("[data-testid='tx-refund-preview']").TextContent);
        cut.Find("[data-testid='tx-category']").Change(CatId);
        cut.Find("[data-testid='tx-bank']").Change(BankId);
        cut.Find("[data-testid='tx-save']").Click();

        cut.WaitForAssertion(() => Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/transactions"));
        var body = await Http.Requests.Single(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/transactions").Content!.ReadAsStringAsync();
        Assert.Contains("\"refund_expected\":true", body);
        Assert.Contains("\"refund_percentage\":30", body);
    }

    [Fact]
    public async Task RefundFlaggedWithoutAPercentage_IsRefusedLocally()
    {
        await SignInAsync();
        StubCatalogs();

        var cut = Render<TransactionForm>();
        cut.WaitForElement("[data-testid='tx-save']");
        cut.Find("[data-testid='tx-type']").Change("unplanned_essential");
        cut.Find("[data-testid='tx-refund-expected']").Change(true);
        cut.Find("[data-testid='tx-payee']").Input("Hospital");
        cut.Find("[data-testid='tx-amount']").Change("100");
        cut.Find("[data-testid='tx-category']").Change(CatId);
        cut.Find("[data-testid='tx-bank']").Change(BankId);
        cut.Find("[data-testid='tx-save']").Click();

        Assert.Contains("Tx_Validation_Refund", cut.WaitForElement("[data-testid='tx-error']").TextContent);
        Assert.DoesNotContain(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/transactions");
    }

    [Fact]
    public async Task MonthDetail_ListsRefunds_AndMarksOneReceived()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, $"/api/months/{MonthId}", $$"""{"id":"{{MonthId}}","year":2026,"month_number":6,"week_count":4,"week1_start_date":"2026-05-28","primary_income_amount":0,"primary_income_currency":"USD","secondary_income_amount":0,"secondary_income_currency":"USD","weeks":[{"week_number":1,"start_date":"2026-05-28","end_date":"2026-06-03"}]}""");
        Http.On(HttpMethod.Get, $"/api/months/{MonthId}/transactions", "[]");
        Http.On(HttpMethod.Get, $"/api/months/{MonthId}/refunds", $$"""[{"id":"{{RefundId}}","month_id":"{{MonthId}}","transaction_id":"dddddddd-0000-0000-0000-000000000004","payee":"Hospital","transaction_date":"2026-06-05","percentage":30,"amount_crc":15000,"amount_usd":30,"status":"pending","inflow_transaction_id":null}]""");
        Http.On(HttpMethod.Put, $"/api/refunds/{RefundId}", $$"""{"id":"{{RefundId}}","status":"received"}""");

        var cut = Render<MonthDetail>(p => p.Add(x => x.Id, Guid.Parse(MonthId)));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='month-refund-row']")));
        var row = cut.Find("[data-testid='month-refund-row']");
        Assert.Contains("Hospital", row.TextContent);
        Assert.Contains("15,000.00", row.TextContent);
        Assert.Contains("Refund_Pending", row.TextContent);
        Assert.Contains("Refund_MarkReceived", cut.Find("[data-testid='refund-toggle']").TextContent);

        // The received date defaults to today and can't precede the purchase; the pick rides along on the flip (ADR-V017).
        var dateInput = cut.Find("[data-testid='refund-received-date']");
        Assert.Equal("2026-06-05", dateInput.GetAttribute("min"));
        Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd"), dateInput.GetAttribute("value"));
        dateInput.Change("2026-07-03");
        cut.Find("[data-testid='refund-toggle']").Click();

        cut.WaitForElement("[data-testid='month-notice']");
        var put = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put);
        Assert.EndsWith($"/api/refunds/{RefundId}", put.RequestUri!.AbsolutePath);
        var body = await put.Content!.ReadAsStringAsync();
        Assert.Contains("\"status\":\"received\"", body);
        Assert.Contains("\"received_date\":\"2026-07-03\"", body);
    }

    [Fact]
    public async Task MonthDetail_ReceivedRefund_ShowsTheDate_AndLinksTheMonthTheInflowLandedIn()
    {
        const string JulyId = "aaaaaaaa-0000-0000-0000-000000000007";
        await SignInAsync();
        Http.On(HttpMethod.Get, $"/api/months/{MonthId}", $$"""{"id":"{{MonthId}}","year":2026,"month_number":6,"week_count":4,"week1_start_date":"2026-05-28","primary_income_amount":0,"primary_income_currency":"USD","secondary_income_amount":0,"secondary_income_currency":"USD","weeks":[{"week_number":1,"start_date":"2026-05-28","end_date":"2026-06-03"}]}""");
        Http.On(HttpMethod.Get, $"/api/months/{MonthId}/transactions", "[]");
        Http.On(HttpMethod.Get, $"/api/months/{MonthId}/refunds", $$"""[{"id":"{{RefundId}}","month_id":"{{MonthId}}","transaction_id":"dddddddd-0000-0000-0000-000000000004","payee":"Hospital","transaction_date":"2026-06-05","percentage":30,"amount_crc":15000,"amount_usd":30,"status":"received","inflow_transaction_id":"dddddddd-0000-0000-0000-000000000009","received_date":"2026-07-03","inflow_month_id":"{{JulyId}}"}]""");
        Http.On(HttpMethod.Put, $"/api/refunds/{RefundId}", $$"""{"id":"{{RefundId}}","status":"pending"}""");

        var cut = Render<MonthDetail>(p => p.Add(x => x.Id, Guid.Parse(MonthId)));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='month-refund-row']")));
        Assert.Contains("Refund_ReceivedOn[", cut.Find("[data-testid='refund-status']").TextContent); // the date rendered in the culture format
        Assert.EndsWith($"/months/{JulyId}", cut.Find("[data-testid='refund-inflow-month']").GetAttribute("href"));
        Assert.Empty(cut.FindAll("[data-testid='refund-received-date']")); // no date picker on a received row
        Assert.Contains("Refund_MarkPending", cut.Find("[data-testid='refund-toggle']").TextContent);

        cut.Find("[data-testid='refund-toggle']").Click();
        cut.WaitForElement("[data-testid='month-notice']");
        var body = await Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put).Content!.ReadAsStringAsync();
        Assert.Contains("\"status\":\"pending\"", body);
        Assert.DoesNotContain("received_date", body);
    }

    [Fact]
    public async Task MonthDetail_LostConcurrentFlip_ShowsTheConflictAndReloads()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, $"/api/months/{MonthId}", $$"""{"id":"{{MonthId}}","year":2026,"month_number":6,"week_count":4,"week1_start_date":"2026-05-28","primary_income_amount":0,"primary_income_currency":"USD","secondary_income_amount":0,"secondary_income_currency":"USD","weeks":[{"week_number":1,"start_date":"2026-05-28","end_date":"2026-06-03"}]}""");
        Http.On(HttpMethod.Get, $"/api/months/{MonthId}/transactions", "[]");
        Http.On(HttpMethod.Get, $"/api/months/{MonthId}/refunds", $$"""[{"id":"{{RefundId}}","month_id":"{{MonthId}}","transaction_id":"dddddddd-0000-0000-0000-000000000004","payee":"Hospital","transaction_date":"2026-06-05","percentage":30,"amount_crc":15000,"amount_usd":30,"status":"pending","inflow_transaction_id":null}]""");
        Http.On(HttpMethod.Put, $"/api/refunds/{RefundId}", """{"error":"refund_status_conflict","message":"changed concurrently"}""", HttpStatusCode.Conflict);

        var cut = Render<MonthDetail>(p => p.Add(x => x.Id, Guid.Parse(MonthId)));
        cut.WaitForElement("[data-testid='refund-toggle']").Click();

        Assert.Contains("Refund_Conflict", cut.WaitForElement("[data-testid='month-error']").TextContent);
        Assert.Equal(2, Http.Requests.Count(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == $"/api/months/{MonthId}/refunds")); // reloaded
    }
}
