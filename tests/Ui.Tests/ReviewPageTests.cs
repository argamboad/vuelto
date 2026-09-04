using System.Net;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Shared.Ui;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>EMAIL-6 UI: the queue renders drafts with the suggestion prefilled and the parsed fields read-only, opens only the blanks, confirm posts the decision and reloads, discard posts, 409 explains and reloads, and the empty state shows.</summary>
public class ReviewPageTests : ComponentTestBase
{
    private const string V1 = "dddddddd-0000-0000-0000-000000000001";
    private const string V2 = "dddddddd-0000-0000-0000-000000000002";
    private const string Cat1 = "cccccccc-0000-0000-0000-000000000001";
    private const string Cat2 = "cccccccc-0000-0000-0000-000000000002";
    private const string Bank1 = "bbbbbbbb-0000-0000-0000-000000000001";
    private const string Categories = $$"""[{"id":"{{Cat1}}","name":"Dining","is_active":true},{"id":"{{Cat2}}","name":"Groceries","is_active":true}]""";
    private const string Banks = $$"""[{"id":"{{Bank1}}","name":"BAC Credomatic","is_active":true}]""";
    private const string Queue = $$"""
        [{"id":"{{V1}}","parsed_bank":"Bac","merchant":"TACO BELL PLAZA REAL C","amount":7620,"currency":"CRC","date":"2026-06-13","bank_id":"{{Bank1}}","card_number":null,"authorization":"662664","reference":null,"transaction_type":"COMPRA","missing_fields":[],"suggested_category_id":"{{Cat1}}","suggested_class":"extraordinary","received_at":"2026-06-16T12:00:00+00:00"},
         {"id":"{{V2}}","parsed_bank":"BN","merchant":null,"amount":null,"currency":null,"date":"2026-06-14","bank_id":"{{Bank1}}","card_number":null,"authorization":null,"reference":"R1","transaction_type":"PAGO","missing_fields":["Merchant","Amount","Currency"],"suggested_category_id":null,"suggested_class":null,"received_at":null}]
        """;

    /// <summary>Always re-query: every change re-renders the card, and a cached element's handlers go stale.</summary>
    private static AngleSharp.Dom.IElement Card(IRenderedComponent<Review> cut, int index) => cut.FindAll("[data-testid='review-voucher']")[index];

    private void StubQueue(string queue = Queue)
    {
        Http.On(HttpMethod.Get, "/api/categories", Categories);
        Http.On(HttpMethod.Get, "/api/banks", Banks);
        Http.On(HttpMethod.Get, "/api/pending-vouchers", queue);
    }

    [Fact]
    public async Task Confirm_CanCreateTheCategoryInline_AndEveryCardSeesIt()
    {
        const string NewCatId = "cccccccc-0000-0000-0000-000000000009";
        await SignInAsync();
        StubQueue();
        Http.On(HttpMethod.Post, "/api/categories", $$"""{"id":"{{NewCatId}}","name":"Viajes","is_active":true}""", HttpStatusCode.Created);
        Http.On(HttpMethod.Post, $"/api/pending-vouchers/{V1}/confirm", $$"""{"transaction_id":"{{V1}}","month_id":"{{V1}}","amount_crc":7620,"amount_usd":15,"remembered":false}""");

        var cut = Render<Review>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='review-voucher']").Count));

        Card(cut, 0).QuerySelector("[data-testid='review-category-new']")!.Click();
        Card(cut, 0).QuerySelector("[data-testid='review-category-new-name']")!.Input("Viajes");
        Card(cut, 0).QuerySelector("[data-testid='review-category-new-save']")!.Click();

        cut.WaitForAssertion(() => Assert.Equal(NewCatId, Card(cut, 0).QuerySelector("[data-testid='review-category']")!.GetAttribute("value")));
        // The page's list is shared: the other card can pick it too.
        Assert.Contains(Card(cut, 1).QuerySelectorAll("[data-testid='review-category'] option"), o => o.GetAttribute("value") == NewCatId);

        Card(cut, 0).QuerySelector("[data-testid='review-confirm']")!.Click();
        cut.WaitForAssertion(() => Assert.Contains("Review_Confirmed", cut.Find("[data-testid='review-notice']").TextContent));
        var body = await Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith("/api/pending-vouchers")).Content!.ReadAsStringAsync();
        Assert.Contains($"\"category_id\":\"{NewCatId}\"", body);
    }

    [Fact]
    public async Task Renders_Drafts_WithTheSuggestionPrefilled_AndOpensOnlyTheBlanks()
    {
        await SignInAsync();
        StubQueue();

        var cut = Render<Review>();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='review-voucher']").Count));
        Assert.Contains("Review_ToReview[2]", cut.Find("[data-testid='review-count']").TextContent);
        var cards = cut.FindAll("[data-testid='review-voucher']");

        Assert.Contains("TACO BELL PLAZA REAL C", cards[0].QuerySelector("[data-testid='review-merchant']")!.TextContent);
        Assert.Contains("₡7,620.00", cards[0].QuerySelector("[data-testid='review-amount']")!.TextContent);
        Assert.Contains("BAC Credomatic", cards[0].TextContent);
        Assert.NotNull(cards[0].QuerySelector("[data-testid='review-suggested']"));
        Assert.Equal(Cat1, cards[0].QuerySelector("[data-testid='review-category']")!.GetAttribute("value"));
        Assert.Equal("extraordinary", cards[0].QuerySelector("[data-testid='review-class']")!.GetAttribute("value"));
        Assert.Null(cards[0].QuerySelector("[data-testid='review-amount-input']")); // parsed → read-only
        Assert.Null(cards[0].QuerySelector("[data-testid='review-missing']"));

        Assert.Contains("Review_UnknownMerchant", cards[1].QuerySelector("[data-testid='review-merchant']")!.TextContent);
        Assert.Contains("Merchant, Amount, Currency", cards[1].QuerySelector("[data-testid='review-missing']")!.TextContent);
        Assert.Null(cards[1].QuerySelector("[data-testid='review-suggested']"));
        Assert.True(string.IsNullOrEmpty(cards[1].QuerySelector("[data-testid='review-category']")!.GetAttribute("value")));
        Assert.Equal("budgeted", cards[1].QuerySelector("[data-testid='review-class']")!.GetAttribute("value"));
        Assert.NotNull(cards[1].QuerySelector("[data-testid='review-payee']"));
        Assert.NotNull(cards[1].QuerySelector("[data-testid='review-amount-input']"));
        Assert.Null(cards[1].QuerySelector("[data-testid='review-date']")); // the date parsed
        Assert.True(cards[1].QuerySelector("[data-testid='review-remember']")!.HasAttribute("disabled")); // nothing to remember
    }

    [Fact]
    public async Task Confirm_PostsTheDecision_WithoutOverridesForParsedFields_AndReloads()
    {
        await SignInAsync();
        StubQueue();
        Http.On(HttpMethod.Post, $"/api/pending-vouchers/{V1}/confirm", $$"""{"transaction_id":"{{V2}}","month_id":"{{V2}}","amount_crc":7620,"amount_usd":15.24,"remembered":true}""");

        var notified = false;
        Services.GetRequiredService<ReviewQueueNotifier>().Changed += () => notified = true;
        var cut = Render<Review>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='review-confirm']").Count));
        Card(cut, 0).QuerySelector("[data-testid='review-class']")!.Change("budgeted");
        Card(cut, 0).QuerySelector("[data-testid='review-remember']")!.Change(true);
        Card(cut, 0).QuerySelector("[data-testid='review-confirm']")!.Click();

        cut.WaitForAssertion(() => Assert.Contains("Review_ConfirmedRemembered", cut.Find("[data-testid='review-notice']").TextContent));
        Assert.True(notified); // the header badge re-counts through the notifier
        var post = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith("/api/pending-vouchers"));
        var body = await post.Content!.ReadAsStringAsync();
        Assert.Contains($"\"category_id\":\"{Cat1}\"", body);
        Assert.Contains("\"transaction_class\":\"budgeted\"", body);
        Assert.Contains("\"remember_merchant\":true", body);
        Assert.Contains("\"original_amount\":null", body);
        Assert.Contains("\"payee\":null", body);
        Assert.Equal(2, Http.Requests.Count(r => r.RequestUri!.AbsolutePath == "/api/pending-vouchers")); // reloaded
    }

    [Fact]
    public async Task Confirm_SendsTheOverrides_ForTheBlanks_AndRequiresACategory()
    {
        await SignInAsync();
        StubQueue();
        Http.On(HttpMethod.Post, $"/api/pending-vouchers/{V2}/confirm", $$"""{"transaction_id":"{{V1}}","month_id":"{{V1}}","amount_crc":5000,"amount_usd":10,"remembered":false}""");

        var cut = Render<Review>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='review-confirm']").Count));
        Card(cut, 1).QuerySelector("[data-testid='review-confirm']")!.Click();
        cut.WaitForAssertion(() => Assert.Contains("Review_CategoryRequired", cut.Find("[data-testid='review-notice']").TextContent));
        Assert.DoesNotContain(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith("/api/pending-vouchers"));

        Card(cut, 1).QuerySelector("[data-testid='review-category']")!.Change(Cat2);
        Card(cut, 1).QuerySelector("[data-testid='review-payee']")!.Change("Pago BN");
        Card(cut, 1).QuerySelector("[data-testid='review-amount-input']")!.Change("5000");
        Card(cut, 1).QuerySelector("[data-testid='review-currency']")!.Change("CRC");
        Card(cut, 1).QuerySelector("[data-testid='review-confirm']")!.Click();

        cut.WaitForAssertion(() => Assert.Contains("Review_Confirmed", cut.Find("[data-testid='review-notice']").TextContent));
        var body = await Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith("/api/pending-vouchers")).Content!.ReadAsStringAsync();
        Assert.Contains($"\"category_id\":\"{Cat2}\"", body);
        Assert.Contains("\"payee\":\"Pago BN\"", body);
        Assert.Contains("\"original_amount\":5000", body);
        Assert.Contains("\"currency\":\"CRC\"", body);
        Assert.Contains("\"transaction_date\":null", body);
    }

    [Fact]
    public async Task Discard_Posts_AndAConflictExplainsAndReloads()
    {
        await SignInAsync();
        StubQueue();
        Http.On(HttpMethod.Post, $"/api/pending-vouchers/{V1}/discard", "", HttpStatusCode.NoContent);
        Http.On(HttpMethod.Post, $"/api/pending-vouchers/{V2}/confirm", """{"error":"not_pending","message":"x"}""", HttpStatusCode.Conflict);

        var cut = Render<Review>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='review-discard']").Count));
        Card(cut, 0).QuerySelector("[data-testid='review-discard']")!.Click();
        cut.WaitForAssertion(() => Assert.Contains("Review_Discarded", cut.Find("[data-testid='review-notice']").TextContent));
        Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == $"/api/pending-vouchers/{V1}/discard");

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='review-voucher']").Count)); // reloaded (the stub still returns both)
        Card(cut, 1).QuerySelector("[data-testid='review-category']")!.Change(Cat1);
        Card(cut, 1).QuerySelector("[data-testid='review-confirm']")!.Click();
        cut.WaitForAssertion(() => Assert.Contains("Review_NotPending", cut.Find("[data-testid='review-notice']").TextContent));
        Assert.Equal(3, Http.Requests.Count(r => r.RequestUri!.AbsolutePath == "/api/pending-vouchers")); // initial + two reloads
    }

    [Fact]
    public async Task EmptyQueue_ShowsTheEmptyState_AndNoCount()
    {
        await SignInAsync();
        StubQueue("[]");
        var cut = Render<Review>();
        cut.WaitForElement("[data-testid='review-empty']");
        Assert.Empty(cut.FindAll("[data-testid='review-count']"));
    }
}
