using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>LEDGER-1/2 pages: the transaction form posts the wire payload and announces the month; the month page lists, edits income, and deletes with confirmation.</summary>
public class LedgerPagesTests : ComponentTestBase
{
    private const string MonthId = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string CatId = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string BankId = "cccccccc-0000-0000-0000-000000000003";
    private const string TxId = "dddddddd-0000-0000-0000-000000000004";

    private void StubCatalogs()
    {
        Http.On(HttpMethod.Get, "/api/categories", $$"""[{"id":"{{CatId}}","name":"Groceries","is_active":true}]""");
        Http.On(HttpMethod.Get, "/api/banks", $$"""[{"id":"{{BankId}}","name":"Cash","is_active":true}]""");
        Http.On(HttpMethod.Get, "/api/envelopes", "[]");
        Http.On(HttpMethod.Get, "/api/exchange-rate", """{"rate":510.45,"source":"live","as_of":"2026-09-03T12:00:00+00:00"}""");
        Http.On(HttpMethod.Get, "/api/months/resolve", """{"month_id":null,"year":2026,"month_number":7,"is_new":true}""");
    }

    [Fact]
    public async Task NewTransaction_PrefillsTheRate_AnnouncesTheMonth_PostsThePayload_AndNavigatesToTheMonth()
    {
        await SignInAsync();
        StubCatalogs();
        Http.On(HttpMethod.Post, "/api/transactions", $$"""{"id":"{{TxId}}","month_id":"{{MonthId}}","payee":"AutoMercado","bank_id":"{{BankId}}","payment_method":"credit_card","original_amount":50000,"currency":"CRC","transaction_date":"2026-07-10","category_id":"{{CatId}}","exchange_rate_used":510.45,"transaction_type":"budgeted","source":"manual","envelope_id":null}""", HttpStatusCode.Created);

        var cut = Render<TransactionForm>();

        cut.WaitForAssertion(() => Assert.Equal("510.45", cut.Find("[data-testid='tx-rate']").GetAttribute("value")));
        Assert.Contains("Tx_GoesToNew[July 2026]", cut.Find("[data-testid='tx-resolve']").TextContent);

        cut.Find("[data-testid='tx-payee']").Input("AutoMercado");
        cut.Find("[data-testid='tx-amount']").Change("50000");
        cut.Find("[data-testid='tx-category']").Change(CatId);
        cut.Find("[data-testid='tx-bank']").Change(BankId);
        cut.Find("[data-testid='tx-save']").Click();

        cut.WaitForAssertion(() => Assert.EndsWith($"/months/{MonthId}", Services.GetRequiredService<NavigationManager>().Uri));
        var post = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/transactions");
        var body = await post.Content!.ReadAsStringAsync();
        Assert.Contains("\"payee\":\"AutoMercado\"", body);
        Assert.Contains("\"original_amount\":50000", body);
        Assert.Contains("\"currency\":\"CRC\"", body);
        Assert.Contains($"\"category_id\":\"{CatId}\"", body);
        Assert.Contains($"\"bank_id\":\"{BankId}\"", body);
        Assert.Contains("\"exchange_rate\":510.45", body);
        Assert.Contains("\"transaction_type\":\"budgeted\"", body);
    }

    [Fact]
    public async Task NewTransaction_MissingBank_IsRefusedLocally_WithoutARequest()
    {
        await SignInAsync();
        StubCatalogs();

        var cut = Render<TransactionForm>();
        cut.WaitForElement("[data-testid='tx-save']");
        cut.Find("[data-testid='tx-payee']").Input("AutoMercado");
        cut.Find("[data-testid='tx-amount']").Change("100");
        cut.Find("[data-testid='tx-category']").Change(CatId);
        cut.Find("[data-testid='tx-save']").Click();

        Assert.Contains("Tx_Validation_Bank", cut.WaitForElement("[data-testid='tx-error']").TextContent);
        Assert.DoesNotContain(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/transactions");
    }

    [Fact]
    public async Task NewTransaction_RateUnavailable_ShowsTheHint_AndRequiresARate()
    {
        await SignInAsync();
        StubCatalogs();
        Http.On(HttpMethod.Get, "/api/exchange-rate", """{"error":"exchange_rate_unavailable","message":"none"}""", HttpStatusCode.ServiceUnavailable);

        var cut = Render<TransactionForm>();

        cut.WaitForAssertion(() => Assert.Contains("Tx_RateUnavailable", cut.Find("[data-testid='tx-rate-hint']").TextContent));
        cut.Find("[data-testid='tx-payee']").Input("X");
        cut.Find("[data-testid='tx-amount']").Change("100");
        cut.Find("[data-testid='tx-category']").Change(CatId);
        cut.Find("[data-testid='tx-bank']").Change(BankId);
        cut.Find("[data-testid='tx-save']").Click();
        Assert.Contains("Tx_Validation_Rate", cut.WaitForElement("[data-testid='tx-error']").TextContent);
    }

    [Fact]
    public async Task EditTransaction_LoadsTheRow_FreezesTheRate_AndPuts()
    {
        await SignInAsync();
        StubCatalogs();
        Http.On(HttpMethod.Get, $"/api/transactions/{TxId}", $$"""{"id":"{{TxId}}","month_id":"{{MonthId}}","payee":"AutoMercado","bank_id":"{{BankId}}","payment_method":"credit_card","original_amount":50000,"currency":"CRC","transaction_date":"2026-07-10","category_id":"{{CatId}}","exchange_rate_used":500,"transaction_type":"budgeted","source":"manual","envelope_id":null}""");
        Http.On(HttpMethod.Put, $"/api/transactions/{TxId}", $$"""{"id":"{{TxId}}","month_id":"{{MonthId}}","payee":"Fresh Market","bank_id":"{{BankId}}","payment_method":"credit_card","original_amount":50000,"currency":"CRC","transaction_date":"2026-07-10","category_id":"{{CatId}}","exchange_rate_used":500,"transaction_type":"budgeted","source":"manual","envelope_id":null}""");

        var cut = Render<TransactionForm>(p => p.Add(x => x.Id, Guid.Parse(TxId)));

        cut.WaitForAssertion(() => Assert.Equal("AutoMercado", cut.Find("[data-testid='tx-payee']").GetAttribute("value")));
        Assert.True(cut.Find("[data-testid='tx-rate']").HasAttribute("disabled"));
        Assert.Contains("Tx_RateFrozen", cut.Find("[data-testid='tx-rate-hint']").TextContent);

        cut.Find("[data-testid='tx-payee']").Input("Fresh Market");
        cut.Find("[data-testid='tx-save']").Click();

        cut.WaitForAssertion(() => Assert.EndsWith($"/months/{MonthId}", Services.GetRequiredService<NavigationManager>().Uri));
        var put = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put);
        var body = await put.Content!.ReadAsStringAsync();
        Assert.Contains("\"payee\":\"Fresh Market\"", body);
        Assert.DoesNotContain("exchange_rate", body); // the rate is frozen — never sent on edit
    }

    [Fact]
    public async Task MonthDetail_ListsWeeksIncomeAndTransactions_DeletesWithConfirmation_AndLeavesWhenTheMonthIsGone()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, $"/api/months/{MonthId}", $$"""{"id":"{{MonthId}}","year":2026,"month_number":7,"week_count":5,"week1_start_date":"2026-06-25","primary_income_amount":3750,"primary_income_currency":"USD","secondary_income_amount":312500,"secondary_income_currency":"CRC","weeks":[{"week_number":1,"start_date":"2026-06-25","end_date":"2026-07-01"},{"week_number":2,"start_date":"2026-07-02","end_date":"2026-07-08"},{"week_number":3,"start_date":"2026-07-09","end_date":"2026-07-15"},{"week_number":4,"start_date":"2026-07-16","end_date":"2026-07-22"},{"week_number":5,"start_date":"2026-07-23","end_date":"2026-07-29"}]}""");
        Http.On(HttpMethod.Get, $"/api/months/{MonthId}/transactions", $$"""[{"id":"{{TxId}}","payee":"AutoMercado","transaction_date":"2026-07-10","category_name":"Groceries","bank_name":"Cash","payment_method":"credit_card","transaction_type":"budgeted","amount_crc":50000,"amount_usd":100,"source":"manual"}]""");
        Http.On(HttpMethod.Delete, $"/api/transactions/{TxId}", "", HttpStatusCode.NoContent);

        var cut = Render<MonthDetail>(p => p.Add(x => x.Id, Guid.Parse(MonthId)));

        cut.WaitForAssertion(() => Assert.Contains("July 2026", cut.Find("[data-testid='month-title']").TextContent));
        Assert.Equal(5, cut.Find("[data-testid='month-weeks']").Children.Length);
        Assert.Equal("3750", cut.Find("[data-testid='inc-primary']").GetAttribute("value"));
        var row = Assert.Single(cut.FindAll("[data-testid='month-tx-row']"));
        Assert.Contains("AutoMercado", row.TextContent);
        Assert.Contains("Groceries", row.TextContent);
        Assert.Contains("Tx_Budgeted", row.TextContent);

        // Delete is two clicks; the first only arms the confirm button.
        cut.Find("[data-testid='month-tx-delete']").Click();
        Assert.DoesNotContain(Http.Requests, r => r.Method == HttpMethod.Delete);
        Http.On(HttpMethod.Get, $"/api/months/{MonthId}", """{"error":"not_found","message":"month not found"}""", HttpStatusCode.NotFound); // it was the last transaction
        cut.Find("[data-testid='month-tx-confirm']").Click();

        cut.WaitForAssertion(() => Assert.EndsWith("/months", Services.GetRequiredService<NavigationManager>().Uri));
        Assert.Single(Http.Requests, r => r.Method == HttpMethod.Delete && r.RequestUri!.AbsolutePath == $"/api/transactions/{TxId}");
    }

    [Fact]
    public async Task MonthDetail_ReloadsWhenTheRouteIdChanges()
    {
        // A link from one month page to another (e.g. a refund's "booked in another month — view", ADR-V017)
        // keeps the component alive with a new Id — the page must fetch the new month, not keep the old one.
        const string OtherId = "aaaaaaaa-0000-0000-0000-000000000007";
        await SignInAsync();
        Http.On(HttpMethod.Get, $"/api/months/{MonthId}", $$"""{"id":"{{MonthId}}","year":2026,"month_number":6,"week_count":4,"week1_start_date":"2026-05-28","primary_income_amount":3000,"primary_income_currency":"USD","secondary_income_amount":0,"secondary_income_currency":"USD","weeks":[{"week_number":1,"start_date":"2026-05-28","end_date":"2026-06-03"}]}""");
        Http.On(HttpMethod.Get, $"/api/months/{MonthId}/transactions", "[]");
        Http.On(HttpMethod.Get, $"/api/months/{OtherId}", $$"""{"id":"{{OtherId}}","year":2026,"month_number":7,"week_count":5,"week1_start_date":"2026-06-25","primary_income_amount":3750,"primary_income_currency":"USD","secondary_income_amount":0,"secondary_income_currency":"USD","weeks":[{"week_number":1,"start_date":"2026-06-25","end_date":"2026-07-01"}]}""");
        Http.On(HttpMethod.Get, $"/api/months/{OtherId}/transactions", "[]");

        var cut = Render<MonthDetail>(p => p.Add(x => x.Id, Guid.Parse(MonthId)));
        cut.WaitForElement("[data-testid='month-no-tx']");
        Assert.Contains("June 2026", cut.Find("[data-testid='month-title']").TextContent);

        cut.Render(p => p.Add(x => x.Id, Guid.Parse(OtherId)));

        cut.WaitForAssertion(() => Assert.Contains("July 2026", cut.Find("[data-testid='month-title']").TextContent));
        Assert.Single(Http.Requests, r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == $"/api/months/{OtherId}");
    }

    [Fact]
    public async Task MonthDetail_SavesIncome()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, $"/api/months/{MonthId}", $$"""{"id":"{{MonthId}}","year":2026,"month_number":6,"week_count":4,"week1_start_date":"2026-05-28","primary_income_amount":3000,"primary_income_currency":"USD","secondary_income_amount":0,"secondary_income_currency":"USD","weeks":[{"week_number":1,"start_date":"2026-05-28","end_date":"2026-06-03"}]}""");
        Http.On(HttpMethod.Get, $"/api/months/{MonthId}/transactions", "[]");
        Http.On(HttpMethod.Put, $"/api/months/{MonthId}/income", $$"""{"id":"{{MonthId}}","year":2026,"month_number":6,"week_count":4,"week1_start_date":"2026-05-28","primary_income_amount":1600000,"primary_income_currency":"CRC","secondary_income_amount":0,"secondary_income_currency":"USD","weeks":null}""");

        var cut = Render<MonthDetail>(p => p.Add(x => x.Id, Guid.Parse(MonthId)));

        cut.WaitForElement("[data-testid='month-no-tx']");
        cut.Find("[data-testid='inc-primary']").Change("1600000");
        cut.Find("[data-testid='inc-primary-cur']").Change("CRC");
        cut.Find("[data-testid='inc-save']").Click();

        cut.WaitForElement("[data-testid='month-notice']");
        var put = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put);
        var body = await put.Content!.ReadAsStringAsync();
        Assert.Contains("\"primary_income_amount\":1600000", body);
        Assert.Contains("\"primary_income_currency\":\"CRC\"", body);
    }

    [Fact]
    public async Task Months_ListsNewestFirst_OrTheEmptyState()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/months", $$"""[{"id":"{{MonthId}}","year":2026,"month_number":7,"week_count":5,"week1_start_date":"2026-06-25"},{"id":"eeeeeeee-0000-0000-0000-000000000005","year":2026,"month_number":6,"week_count":4,"week1_start_date":"2026-05-28"}]""");

        var cut = Render<Months>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='month-row']").Count));
        var rows = cut.FindAll("[data-testid='month-row']");
        Assert.Contains("July 2026", rows[0].TextContent);
        Assert.Contains("Months_Weeks[5]", rows[0].TextContent);
        Assert.Contains("June 2026", rows[1].TextContent);

        Http.On(HttpMethod.Get, "/api/months", "[]");
        var empty = Render<Months>();
        empty.WaitForElement("[data-testid='months-empty']");
    }
}
