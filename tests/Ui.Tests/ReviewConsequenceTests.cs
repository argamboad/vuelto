using Bunit;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// SKIN-6: the consequence rail — the redesign's fifth decision (say what an action will do before it happens)
/// in the one place it earns the most. Two rules are asserted here rather than trusted: the sentence states the
/// booking BEFORE the button that performs it, and Confirm is blocked visibly, naming what is still missing,
/// instead of letting the click through to fail on submit.
/// </summary>
public class ReviewConsequenceTests : ComponentTestBase
{
    private const string Complete = "dddddddd-0000-0000-0000-000000000001";
    private const string Incomplete = "dddddddd-0000-0000-0000-000000000002";
    private const string Cat1 = "cccccccc-0000-0000-0000-000000000001";
    private const string Bank1 = "bbbbbbbb-0000-0000-0000-000000000001";

    private const string Categories = $$"""[{"id":"{{Cat1}}","name":"Groceries","is_active":true}]""";
    private const string Banks = $$"""[{"id":"{{Bank1}}","name":"BAC","is_active":true}]""";

    private const string Queue = $$"""
        [{"id":"{{Complete}}","merchant":"Automercado Escazú","amount":48320,"currency":"CRC","date":"2026-09-09","bank_id":"{{Bank1}}","card_number":"************4417","card_brand":"VISA","transaction_type":"COMPRA","missing_fields":[],"suggested_category_id":"{{Cat1}}","suggested_class":"budgeted","received_at":"2026-09-09T18:42:00+00:00"},
         {"id":"{{Incomplete}}","merchant":null,"amount":3699,"currency":"CRC","date":null,"bank_id":"{{Bank1}}","transaction_type":"PAGO","missing_fields":["Merchant","Date"],"suggested_category_id":null,"suggested_class":null,"received_at":null}]
        """;

    private async Task<IRenderedComponent<Review>> QueueAsync()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/categories", Categories);
        Http.On(HttpMethod.Get, "/api/banks", Banks);
        Http.On(HttpMethod.Get, "/api/pending-vouchers", Queue);
        Http.On(HttpMethod.Get, "/api/months/resolve", """{"id":null,"year":2026,"month_number":9,"is_new":true}""");
        var cut = Render<Review>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='review-voucher']").Count));
        return cut;
    }

    private static AngleSharp.Dom.IElement Draft(IRenderedComponent<Review> cut, int i) =>
        cut.FindAll("[data-testid='review-voucher']")[i];

    [Fact]
    public async Task ACompleteDraftStatesWhatConfirmingWillBook()
    {
        var cut = await QueueAsync();
        var body = Draft(cut, 0).QuerySelector($"[data-testid='consequence-{Complete}-body']")!;

        // Amount, category, class — and the PAY-CYCLE month, which is the part not readable off the row.
        Assert.Contains("Review_ConfirmingWillBook", body.TextContent);
        Assert.Contains("₡48,320.00", body.TextContent);
        Assert.Contains("Groceries", body.TextContent);
        Assert.Contains("September 2026", body.TextContent);
    }

    [Fact]
    public async Task TheConsequenceIsReadBeforeTheButtonThatPerformsIt()
    {
        var cut = await QueueAsync();
        var draft = Draft(cut, 0);
        var confirm = draft.QuerySelector("[data-testid='review-confirm']")!;

        // Announced on activation, not merely present somewhere on the row.
        Assert.Equal($"consequence-{Complete}-body", confirm.GetAttribute("aria-describedby"));

        var html = draft.InnerHtml;
        Assert.True(html.IndexOf($"consequence-{Complete}-body", StringComparison.Ordinal)
                    < html.IndexOf("review-confirm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnIncompleteDraftBlocksConfirmVisibly_AndNamesWhatIsMissing()
    {
        var cut = await QueueAsync();
        var draft = Draft(cut, 1);

        Assert.True(draft.QuerySelector("[data-testid='review-confirm']")!.HasAttribute("disabled"));

        var body = draft.QuerySelector($"[data-testid='consequence-{Incomplete}-body']")!.TextContent;
        Assert.Contains("Review_ConfirmingBlocked", body);
        Assert.Contains("Tx_Category", body);   // no suggestion on this draft
        Assert.Contains("Tx_Payee", body);      // the parser could not read it
        Assert.Contains("Tx_Date", body);
        // The amount parsed fine, so it is NOT listed as missing.
        Assert.DoesNotContain("Tx_Amount", body);
    }

    [Fact]
    public async Task FillingTheBlanksUnblocksConfirm_WithoutLeavingTheRow()
    {
        var cut = await QueueAsync();

        Draft(cut, 1).QuerySelector("[data-testid='review-payee']")!.Change("Farmacia Fischel");
        Draft(cut, 1).QuerySelector("[data-testid='review-date']")!.Change("2026-09-07");
        Draft(cut, 1).QuerySelector("[data-testid='review-category']")!.Change(Cat1);

        cut.WaitForAssertion(() =>
            Assert.False(Draft(cut, 1).QuerySelector("[data-testid='review-confirm']")!.HasAttribute("disabled")));
        Assert.Contains("Review_ConfirmingWillBook",
            Draft(cut, 1).QuerySelector($"[data-testid='consequence-{Incomplete}-body']")!.TextContent);
    }

    [Fact]
    public async Task TheParserNoticeNamesTheFieldsInTheUsersWords_AndTheInputsPointAtIt()
    {
        var cut = await QueueAsync();
        var draft = Draft(cut, 1);

        var notice = draft.QuerySelector("[data-testid='review-missing']")!;
        Assert.Contains("Review_MissingFields", notice.TextContent);
        Assert.Contains("Tx_Payee", notice.TextContent);

        // Every input it explains points back at it, and says it is invalid while still blank.
        var payee = draft.QuerySelector("[data-testid='review-payee']")!;
        Assert.Equal(notice.GetAttribute("id"), payee.GetAttribute("aria-describedby"));
        Assert.Equal("true", payee.GetAttribute("aria-invalid"));

        // A complete draft gets no notice at all.
        Assert.Null(Draft(cut, 0).QuerySelector("[data-testid='review-missing']"));
    }

    [Fact]
    public async Task AllThreeClassesAreOfferedAsChips_BecauseUnplannedOpensTheRefundPath()
    {
        var cut = await QueueAsync();
        var draft = Draft(cut, 0);

        Assert.NotNull(draft.QuerySelector("[data-testid='review-class-budgeted']"));
        Assert.NotNull(draft.QuerySelector("[data-testid='review-class-extraordinary']"));
        Assert.NotNull(draft.QuerySelector("[data-testid='review-class-unplanned_essential']"));
        Assert.NotNull(draft.QuerySelector("fieldset"));   // a real radio group, not styled buttons

        // The handout's frame draws only two chips; picking the third is what reveals the refund switch.
        Assert.Null(draft.QuerySelector("[data-testid='review-refund-expected']"));
        draft.QuerySelector("[data-testid='review-class-unplanned_essential']")!.Change(true);
        Assert.NotNull(Draft(cut, 0).QuerySelector("[data-testid='review-refund-expected']"));
    }

    [Fact]
    public async Task TheDraftNamesItselfForAScreenReader()
    {
        var cut = await QueueAsync();
        var label = Draft(cut, 0).GetAttribute("aria-label")!;
        Assert.Contains("Automercado Escazú", label);
        Assert.Contains("₡48,320.00", label);
    }

    [Fact]
    public async Task TheMonthIsResolvedOncePerDistinctDate_NotOncePerDraft()
    {
        var cut = await QueueAsync();
        // Two drafts, one with a date and one without → exactly one resolve call.
        Assert.Single(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/months/resolve");
        Assert.Contains("September 2026",
            Draft(cut, 0).QuerySelector($"[data-testid='consequence-{Complete}-body']")!.TextContent);
    }

    [Fact]
    public async Task AnUnreachableResolveDropsTheMonthClause_RatherThanBlockingTheQueue()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/categories", Categories);
        Http.On(HttpMethod.Get, "/api/banks", Banks);
        Http.On(HttpMethod.Get, "/api/pending-vouchers", Queue);
        Http.On(HttpMethod.Get, "/api/months/resolve", """{"error":"boom"}""", System.Net.HttpStatusCode.InternalServerError);

        var cut = Render<Review>();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='review-voucher']").Count));

        var body = Draft(cut, 0).QuerySelector($"[data-testid='consequence-{Complete}-body']")!.TextContent;
        Assert.Contains("Review_ConfirmingWillBookNoMonth", body);
        Assert.Contains("Groceries", body);
    }
}
