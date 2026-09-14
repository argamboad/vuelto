using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// SKIN-7: twelve stacked fields become amount-first, three named groups, and a rail that says where the
/// transaction lands before it is saved. The behaviour the existing LedgerPagesTests cover — the payload,
/// the rate rules, the month resolve — is unchanged; this pins the SHAPE, and the three things it adds:
/// amount and currency as one input group, all five classes as chips with a hint, and the rail.
/// </summary>
public class TransactionFormShapeTests : ComponentTestBase
{
    private const string Cat = "cccccccc-0000-0000-0000-000000000001";
    private const string Bank = "bbbbbbbb-0000-0000-0000-000000000001";
    private const string Card = "eeeeeeee-0000-0000-0000-000000000005";
    private const string BanklessCard = "eeeeeeee-0000-0000-0000-000000000006";
    private const string TxId = "dddddddd-0000-0000-0000-000000000009";

    private async Task<IRenderedComponent<TransactionForm>> FormAsync()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/categories", $$"""[{"id":"{{Cat}}","name":"Groceries","is_active":true}]""");
        Http.On(HttpMethod.Get, "/api/banks", $$"""[{"id":"{{Bank}}","name":"BAC","is_active":true}]""");
        Http.On(HttpMethod.Get, "/api/cards", $$"""[{"id":"{{Card}}","name":"Casa VISA ····4417","kind":"debit","bank_id":"{{Bank}}","is_active":true},{"id":"{{BanklessCard}}","name":"AMEX ····1001","kind":"credit","bank_id":null,"is_active":true}]""");
        Http.On(HttpMethod.Get, "/api/envelopes", "[]");
        Http.On(HttpMethod.Get, "/api/exchange-rate", """{"rate":540.11,"source":"live","as_of":"2026-09-12T12:00:00+00:00","buy":538,"sell":540.11}""");
        Http.On(HttpMethod.Get, "/api/months/resolve", """{"id":null,"year":2026,"month_number":9,"is_new":true}""");
        Http.On(HttpMethod.Post, "/api/transactions", $$"""{"id":"{{TxId}}","month_id":"{{TxId}}","payee":"x","bank_id":"{{Bank}}","payment_method":"credit_card","original_amount":1,"currency":"CRC","transaction_date":"2026-09-12","category_id":"{{Cat}}","exchange_rate_used":540.11,"transaction_type":"budgeted","source":"manual","envelope_id":null,"refund_expected":false,"refund_percentage":null}""", System.Net.HttpStatusCode.Created);

        var cut = Render<TransactionForm>();
        cut.WaitForElement("[data-testid='tx-form']");
        return cut;
    }

    private async Task<string> SavedBodyAsync(IRenderedComponent<TransactionForm> cut)
    {
        cut.Find("[data-testid='tx-payee']").Input("Hospital");
        cut.Find("[data-testid='tx-amount-field-input']").Change("50000");
        cut.Find("[data-testid='tx-category']").Change(Cat);
        cut.Find("[data-testid='tx-save']").Click();
        cut.WaitForAssertion(() => Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/transactions"));
        var post = Http.Requests.Single(r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/transactions");
        return await post.Content!.ReadAsStringAsync();
    }

    [Fact]
    public async Task AmountAndCurrencyAreOneInputGroup_WithTheConversionUnderThem()
    {
        var cut = await FormAsync();

        var group = cut.Find("[data-testid='tx-amount-field'] .input-group");
        Assert.NotNull(group.QuerySelector("[data-testid='tx-amount-field-input']"));
        Assert.NotNull(group.QuerySelector("[data-testid='tx-amount-field-currency-CRC']"));
        Assert.NotNull(group.QuerySelector("[data-testid='tx-amount-field-currency-USD']"));

        cut.Find("[data-testid='tx-amount-field-input']").Change("48320");
        cut.WaitForAssertion(() =>
        {
            var conv = cut.Find("[data-testid='tx-amount-field-conversion']").TextContent;
            Assert.Contains("Tx_ConvertedAt", conv);   // "= $89.46 at ₡540.11 per $1 · frozen when you save"
            // Spending colones resolves the BUY side (ADR-V019): 48,320 / 538.
            Assert.Contains("$89.81", conv);
        });

        // The conversion line describes the input, so a screen reader reaching the field is told the rate.
        Assert.Equal("tx-amount-field-conversion",
            cut.Find("[data-testid='tx-amount-field-input']").GetAttribute("aria-describedby"));
    }

    [Fact]
    public async Task AllFiveClassesAreChips_EachWithALineSayingWhatItDoes()
    {
        var cut = await FormAsync();

        foreach (var c in new[] { "budgeted", "extraordinary", "unplanned_essential", "inflow", "envelope_contribution" })
        {
            Assert.NotNull(cut.Find($"[data-testid='tx-type-{c}']"));
        }
        Assert.NotNull(cut.Find("[data-testid='tx-type'] fieldset, fieldset[data-testid='tx-type']"));

        Assert.Contains("Tx_ClassHint_Budgeted", cut.Find("[data-testid='tx-class-hint']").TextContent);
        cut.Find("[data-testid='tx-type-inflow']").Change(true);
        Assert.Contains("Tx_ClassHint_Inflow", cut.Find("[data-testid='tx-class-hint']").TextContent);
    }

    [Fact]
    public async Task EachClassStillOpensItsOwnPath()
    {
        var cut = await FormAsync();

        // Unplanned → the expected-refund fields.
        Assert.Empty(cut.FindAll("[data-testid='tx-refund-expected']"));
        cut.Find("[data-testid='tx-type-unplanned_essential']").Change(true);
        Assert.NotNull(cut.Find("[data-testid='tx-refund-expected']"));

        // Envelope → the bucket picker. Dropping either chip would remove the path entirely.
        cut.Find("[data-testid='tx-type-envelope_contribution']").Change(true);
        Assert.Empty(cut.FindAll("[data-testid='tx-refund-expected']"));
        Assert.NotNull(cut.Find("[data-testid='tx-envelope']"));
    }

    [Fact]
    public async Task TheRailSaysWhereItLands_AndBothSidesOfTheMoney()
    {
        var cut = await FormAsync();
        cut.Find("[data-testid='tx-amount-field-input']").Change("48320");

        cut.WaitForAssertion(() => Assert.Contains("Tx_GoesToNew", cut.Find("[data-testid='tx-resolve']").TextContent));

        var money = cut.Find("[data-testid='tx-preview']").TextContent;
        Assert.Contains("₡48,320.00", money);
        Assert.Contains("$89.81", money);
        Assert.Contains("538.00", money);
    }

    [Fact]
    public async Task TheRailNamesTheWeek_AndWhatThePurchaseDoesToItsBudgetLine()
    {
        const string Month = "aaaaaaaa-0000-0000-0000-000000000001";
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/categories", $$"""[{"id":"{{Cat}}","name":"Groceries","is_active":true}]""");
        Http.On(HttpMethod.Get, "/api/banks", $$"""[{"id":"{{Bank}}","name":"BAC","is_active":true}]""");
        Http.On(HttpMethod.Get, "/api/cards", "[]");
        Http.On(HttpMethod.Get, "/api/envelopes", "[]");
        Http.On(HttpMethod.Get, "/api/exchange-rate", """{"rate":500,"source":"live","as_of":"2026-09-12T12:00:00+00:00","buy":500,"sell":500}""");
        // An existing month, with the week the date falls in; its summary carries the line's category, so the purchase can be matched to it.
        Http.On(HttpMethod.Get, "/api/months/resolve", $$"""{"month_id":"{{Month}}","year":2026,"month_number":9,"is_new":false,"week_number":2}""");
        Http.On(HttpMethod.Get, $"/api/months/{Month}/summary", $$"""{"month":{"id":"{{Month}}","year":2026,"month_number":9,"week_count":5,"week1_start_date":"2026-08-27","last_day":"2026-09-30"},"exchange_rate":500,"rate_unavailable":false,"summary":{"fixed_expenses":[{"name":"Groceries","budget":{"crc":60000,"usd":120},"actual":{"crc":8000,"usd":16},"budget_currency":"CRC","category_id":"{{Cat}}"}],"variable_expenses":[]} }""");

        var cut = Render<TransactionForm>();
        cut.WaitForAssertion(() => Assert.Contains("Tx_GoesToWeek[September 2026, 2]", cut.Find("[data-testid='tx-resolve']").TextContent));
        Assert.Empty(cut.FindAll("[data-testid='tx-line-impact']")); // no category yet, nothing to say

        cut.Find("[data-testid='tx-category']").Change(Cat);
        cut.Find("[data-testid='tx-amount-field-input']").Change("50000");
        cut.WaitForAssertion(() => Assert.Contains("Tx_LineImpact[Groceries, ₡8,000.00, ₡60,000.00, ₡58,000.00]", cut.Find("[data-testid='tx-line-impact']").TextContent));

        // A dollar purchase against a colón line is converted at today's rate before it is added.
        cut.Find("[data-testid='tx-amount-field-currency-USD']").Change(true);
        cut.Find("[data-testid='tx-amount-field-input']").Change("10");
        cut.WaitForAssertion(() => Assert.Contains("₡13,000.00]", cut.Find("[data-testid='tx-line-impact']").TextContent));

        // A class that is not budgeted spending has no line to land in.
        cut.Find("[data-testid='tx-type-extraordinary']").Change(true);
        Assert.Empty(cut.FindAll("[data-testid='tx-line-impact']"));
    }

    [Fact]
    public async Task TheRailPrecedesTheSaveButtonItActsOn()
    {
        var cut = await FormAsync();
        var rail = cut.Find("[data-testid='tx-rail']");
        var html = rail.InnerHtml;

        Assert.True(html.IndexOf("tx-rail-body", StringComparison.Ordinal)
                    < html.IndexOf("tx-save", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveStaysEnabled_AndSurfacesValidationOnSubmit()
    {
        // The build notes are explicit: this form does NOT go quietly dead — the opposite rule from the
        // review queue, where the blocker is a parsed blank the user cannot argue with.
        var cut = await FormAsync();
        Assert.False(cut.Find("[data-testid='tx-save']").HasAttribute("disabled"));

        cut.Find("[data-testid='tx-save']").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid='tx-error']")));
        Assert.DoesNotContain(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/transactions");
    }

    [Fact]
    public async Task PickingACardSetsTheBankAndTheMethod_AndLocksThemUntilTheCardChanges()
    {
        // Owner, 2026-09-14: a card already knows its bank and how the money leaves, so picking one decides
        // both — shown as text, not as pickers you could contradict. "No card" opens them again (cash, transfers).
        var cut = await FormAsync();
        Assert.NotNull(cut.Find("[data-testid='tx-bank']"));
        Assert.NotNull(cut.Find("[data-testid='tx-method']"));
        Assert.Empty(cut.FindAll("[data-testid='tx-from-card']"));

        cut.Find("[data-testid='tx-card']").Change(Card);

        Assert.Empty(cut.FindAll("select[data-testid='tx-bank']"));
        Assert.Empty(cut.FindAll("select[data-testid='tx-method']"));
        Assert.Contains("BAC", cut.Find("[data-testid='tx-bank-locked']").TextContent);
        Assert.Contains("Tx_BankAccount", cut.Find("[data-testid='tx-method-locked']").TextContent);
        Assert.Contains("Tx_FromCard", cut.Find("[data-testid='tx-from-card']").TextContent);

        var body = await SavedBodyAsync(cut);
        Assert.Contains($"\"bank_id\":\"{Bank}\"", body);
        Assert.Contains("\"payment_method\":\"bank_account\"", body);
        Assert.Contains($"\"card_id\":\"{Card}\"", body);
    }

    [Fact]
    public async Task NoCardAgain_OpensBankAndMethodBackUp()
    {
        var cut = await FormAsync();
        cut.Find("[data-testid='tx-card']").Change(Card);
        cut.Find("[data-testid='tx-card']").Change("");

        Assert.NotNull(cut.Find("select[data-testid='tx-bank']"));
        Assert.NotNull(cut.Find("select[data-testid='tx-method']"));
        Assert.Empty(cut.FindAll("[data-testid='tx-from-card']"));
    }

    [Fact]
    public async Task ACardWithNoBankOnRecord_LeavesTheBankOpen_AndPointsAtCards()
    {
        var cut = await FormAsync();
        cut.Find("[data-testid='tx-card']").Change(BanklessCard);

        Assert.NotNull(cut.Find("select[data-testid='tx-bank']"));
        Assert.Equal("credit_card", cut.Find("select[data-testid='tx-method']").GetAttribute("value")); // the kind still sets the method
        Assert.Empty(cut.FindAll("[data-testid='tx-from-card']"));
        var hint = cut.Find("[data-testid='tx-card-no-bank']");
        Assert.Contains("Tx_CardNoBank", hint.TextContent);
        Assert.Equal("/cards", hint.QuerySelector("a")!.GetAttribute("href"));
    }

    [Fact]
    public async Task TheHowGroupReadsCardThenBankThenMethod()
    {
        // The card decides the other two, so it comes first.
        var cut = await FormAsync();
        var html = cut.Find("[data-testid='tx-form']").InnerHtml;
        Assert.True(html.IndexOf("tx-card", StringComparison.Ordinal) < html.IndexOf("tx-bank", StringComparison.Ordinal));
        Assert.True(html.IndexOf("tx-bank", StringComparison.Ordinal) < html.IndexOf("tx-method", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EditingARowPaidWithACard_ShowsItsBankAndMethodAsTheCards()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/categories", $$"""[{"id":"{{Cat}}","name":"Groceries","is_active":true}]""");
        Http.On(HttpMethod.Get, "/api/banks", $$"""[{"id":"{{Bank}}","name":"BAC","is_active":true}]""");
        Http.On(HttpMethod.Get, "/api/cards", $$"""[{"id":"{{Card}}","name":"Casa VISA ····4417","kind":"debit","bank_id":"{{Bank}}","is_active":true}]""");
        Http.On(HttpMethod.Get, "/api/envelopes", "[]");
        Http.On(HttpMethod.Get, "/api/exchange-rate", """{"rate":540.11,"source":"live","as_of":"2026-09-12T12:00:00+00:00","buy":538,"sell":540.11}""");
        Http.On(HttpMethod.Get, "/api/months/resolve", """{"id":null,"year":2026,"month_number":9,"is_new":true}""");
        Http.On(HttpMethod.Get, $"/api/transactions/{TxId}", $$"""{"id":"{{TxId}}","month_id":"{{TxId}}","payee":"Hospital","bank_id":"{{Bank}}","card_id":"{{Card}}","notes":null,"payment_method":"bank_account","original_amount":50000,"currency":"CRC","transaction_date":"2026-09-12","category_id":"{{Cat}}","exchange_rate_used":540.11,"transaction_type":"unplanned_essential","source":"manual","envelope_id":null,"refund_expected":true,"refund_percentage":30,"refund_notes":"CASE-7, lent to Diego"}""");

        var cut = Render<TransactionForm>(p => p.Add(x => x.Id, Guid.Parse(TxId)));
        cut.WaitForElement("[data-testid='tx-form']");

        // Stored values agree with the card, so they show as the card's; and the refund notes prefill.
        Assert.Contains("BAC", cut.Find("[data-testid='tx-bank-locked']").TextContent);
        Assert.Contains("Tx_BankAccount", cut.Find("[data-testid='tx-method-locked']").TextContent);
        Assert.Equal("CASE-7, lent to Diego", cut.Find("[data-testid='tx-refund-notes']").GetAttribute("value")); // a bound textarea carries its text as value
    }

    [Fact]
    public async Task ARefundAsksForItsNotes_AsBigAsTheTransactionsOwn_AndSendsThemAlong()
    {
        // Why you expect it back (case number and all) used to be reachable only from the month page, after the fact.
        // Owner, 2026-09-14: ONE notes field, no separate Case No. — a textarea like the transaction's own notes.
        var cut = await FormAsync();
        cut.Find("[data-testid='tx-type-unplanned_essential']").Change(true);
        Assert.Empty(cut.FindAll("[data-testid='tx-refund-notes']"));
        cut.Find("[data-testid='tx-refund-expected']").Change(true);

        cut.Find("[data-testid='tx-refund-pct']").Change("30");
        var notes = cut.Find("textarea[data-testid='tx-refund-notes']");
        Assert.Equal("250", notes.GetAttribute("maxlength"));
        Assert.Equal("1", notes.GetAttribute("rows")); // one row, the percentage's height; the user can drag it taller
        Assert.Contains("Tx_RefundNotes", cut.Find("label[for='tx-refund-notes']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='tx-refund-case']"));
        notes.Input("CASE-7, lent to Diego"); // ASCII on purpose: the JSON body escapes anything else as a unicode sequence
        Assert.Contains("21/250", cut.Find("[data-testid='tx-refund-notes-count']").TextContent);

        cut.Find("[data-testid='tx-bank']").Change(Bank);
        var body = await SavedBodyAsync(cut);
        Assert.Contains("\"refund_percentage\":30", body);
        Assert.DoesNotContain("refund_case_number", body);
        Assert.Contains("\"refund_notes\":\"CASE-7, lent to Diego\"", body);
    }

    [Fact]
    public async Task TheGroupsNameThemselves_SoTwelveFieldsReadAsThreeDecisions()
    {
        var cut = await FormAsync();
        var text = cut.Find("[data-testid='tx-form']").TextContent;
        Assert.Contains("Tx_GroupWhat", text);
        Assert.Contains("Tx_GroupHow", text);
        Assert.Contains("Tx_BeforeYouSave", cut.Find("[data-testid='tx-rail']").TextContent);
    }

    [Fact]
    public async Task EverythingTheFramesNeverDrewIsStillHere()
    {
        var cut = await FormAsync();

        Assert.NotNull(cut.Find("[data-testid='tx-notes']"));
        Assert.NotNull(cut.Find("[data-testid='tx-notes-count']"));
        Assert.NotNull(cut.Find("[data-testid='tx-rate']"));
        Assert.NotNull(cut.Find("[data-testid='tx-rate-hint']"));
        Assert.NotNull(cut.Find("[data-testid='tx-date']"));
        Assert.NotNull(cut.Find("[data-testid='tx-cancel']"));
    }
}
