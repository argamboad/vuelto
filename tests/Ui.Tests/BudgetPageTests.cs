using System.Net;
using Bunit;
using Vuelto.Shared.Ui.Components;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// EXPENSES-1 UI, presented per SKIN-9: a commitment header (planned total, share of a typical income, a
/// fixed/variable bar), two lists of grid rows with a drag handle and a move menu, and one dialog for
/// creating and editing — with the single-currency payload, the active-set reorder PUT and the inactive-clash
/// Reactivate offer all preserved from the table it replaces.
/// </summary>
public class BudgetPageTests : ComponentTestBase
{
    private const string Cat1 = "bbbbbbbb-0000-0000-0000-000000000001";
    private const string Cat2 = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string L1 = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string L2 = "aaaaaaaa-0000-0000-0000-000000000002";
    private const string L3 = "aaaaaaaa-0000-0000-0000-000000000003";
    private const string V1 = "aaaaaaaa-0000-0000-0000-000000000011";
    private const string Fixed = $$"""
        [{"id":"{{L1}}","name":"Mortgage","budget_crc":300000,"budget_usd":0,"payment_method":"bank_account","category_id":"{{Cat1}}","sort_order":0,"is_active":true},
         {"id":"{{L2}}","name":"Netflix","budget_crc":0,"budget_usd":13,"payment_method":"credit_card","category_id":"{{Cat2}}","sort_order":1,"is_active":true},
         {"id":"{{L3}}","name":"Old","budget_crc":5000,"budget_usd":0,"payment_method":"credit_card","category_id":"{{Cat2}}","sort_order":2,"is_active":false}]
        """;
    private const string Variable = $$"""
        [{"id":"{{V1}}","name":"Groceries","budget_crc":100000,"budget_usd":0,"payment_method":"credit_card","category_id":"{{Cat2}}","sort_order":0,"is_active":true}]
        """;

    private static readonly List<CategoryOption> Categories = [new(Guid.Parse(Cat1), "Housing"), new(Guid.Parse(Cat2), "Entertainment")];

    /// <summary>Everything the page fetches: catalogs, both lists, the rate and the income lines (₡250,000 weekly + $250 twice a month = ₡1,000,000 + $500 over four weeks).</summary>
    private void StubPage(string fixedList = Fixed, string variableList = Variable, bool rate = true, bool income = true)
    {
        Http.On(HttpMethod.Get, "/api/categories", $$"""[{"id":"{{Cat1}}","name":"Housing","is_active":true},{"id":"{{Cat2}}","name":"Entertainment","is_active":true}]""");
        Http.On(HttpMethod.Get, "/api/expenses/fixed", fixedList);
        Http.On(HttpMethod.Get, "/api/expenses/variable", variableList);
        if (rate) Http.On(HttpMethod.Get, "/api/exchange-rate", """{"rate":500,"source":"live","as_of":"2026-09-03T12:00:00+00:00"}""");
        Http.On(HttpMethod.Get, "/api/incomes", income
            ? """[{"id":"11111111-0000-0000-0000-000000000001","name":"Salary","member_user_id":null,"currency":"CRC","kind":"fixed","pay_period":"weekly","amount":250000,"pay_days":null,"sort_order":0,"is_active":true,"needs_review":false},{"id":"11111111-0000-0000-0000-000000000002","name":"Side job","member_user_id":null,"currency":"USD","kind":"variable","pay_period":"biweekly","amount":250,"pay_days":[15,31],"sort_order":1,"is_active":true,"needs_review":false}]"""
            : "[]");
    }

    private IRenderedComponent<Budget> RenderPage()
    {
        var cut = Render<Budget>();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid='exp-fixed'] [data-testid='exp-row']").Count));
        return cut;
    }

    // ---------------------------------------------------------------- the commitment header

    [Fact]
    public async Task Header_SumsTheActiveLinesAtTodaysRate_AndStatesTheShareOfATypicalIncome()
    {
        await SignInAsync();
        StubPage();
        var cut = RenderPage();

        // 300,000 + 13 × 500 (fixed, active only) + 100,000 (variable) = 406,500; income 1,000,000 + 500 × 500 = 1,250,000 → 33%.
        cut.WaitForAssertion(() => Assert.Equal("₡406,500.00", cut.Find("[data-testid='budget-planned']").TextContent.Trim()));
        Assert.Contains("Budget_ShareOfIncome[33]", cut.Find("[data-testid='budget-share']").TextContent);
        Assert.Equal("₡1,250,000.00", cut.Find("[data-testid='budget-income']").TextContent.Trim());
        var segments = cut.FindAll("[data-testid='budget-bar-segment']");
        Assert.Equal(2, segments.Count); // fixed, then variable — the dashboard's two-tone split
        Assert.Contains("Budget_Fixed", cut.Find("[data-testid='budget-bar-legend']").TextContent);
        Assert.Contains("₡306,500.00", cut.Find("[data-testid='budget-bar-legend']").TextContent);
        Assert.Contains("Budget_AtTodaysRate", cut.Find("[data-testid='budget-summary']").TextContent); // a projection says so
    }

    [Fact]
    public async Task Header_WithoutIncomeLines_ShowsThePlannedTotalAndNoShare()
    {
        await SignInAsync();
        StubPage(income: false);
        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Equal("₡406,500.00", cut.Find("[data-testid='budget-planned']").TextContent.Trim()));
        Assert.Empty(cut.FindAll("[data-testid='budget-share']"));
        Assert.Empty(cut.FindAll("[data-testid='budget-income']"));
        Assert.Equal(2, cut.FindAll("[data-testid='budget-bar-segment']").Count); // the bar still draws, as a share of the plan itself
    }

    [Fact]
    public async Task Header_WithoutARate_KeepsEachCurrencyOnItsOwnSide()
    {
        await SignInAsync();
        StubPage(rate: false); // /api/exchange-rate → 404: nothing can be converted, nothing is guessed
        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Equal("₡400,000.00 + $13.00", cut.Find("[data-testid='budget-planned']").TextContent.Trim()));
        Assert.Empty(cut.FindAll("[data-testid='budget-share']"));
    }

    [Fact]
    public async Task Header_FollowsTheShowInPreference()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/display-settings", """{"display_currency":"USD"}""");
        StubPage();
        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.Equal("$813.00", cut.Find("[data-testid='budget-planned']").TextContent.Trim()));
        Assert.Contains("₡300,000.00", cut.Find("[data-testid='exp-row-budget']").TextContent); // a line stays in its own currency
    }

    // ---------------------------------------------------------------- rows

    [Fact]
    public async Task Rows_AreGridRows_WithTheLineCurrency_APaidWithChip_ASubLine_AndInactiveStruckThrough()
    {
        await SignInAsync();
        StubPage();
        var cut = RenderPage();

        Assert.Empty(cut.FindAll("[data-testid='exp-fixed'] table")); // rows are drag targets, not table rows
        var rows = cut.FindAll("[data-testid='exp-fixed'] [data-testid='exp-row']");
        Assert.Equal("Mortgage", rows[0].QuerySelector("[data-testid='exp-row-name']")!.TextContent.Trim());
        Assert.Equal("₡300,000.00", rows[0].QuerySelector("[data-testid='exp-row-budget']")!.TextContent.Trim());
        Assert.Equal("Housing", rows[0].QuerySelector("[data-testid='exp-row-category']")!.TextContent.Trim());
        // A plan names no bank (owner, 2026-09-14): the chip is the method alone.
        Assert.Equal("Budget_MethodAccount", rows[0].QuerySelector("[data-testid='exp-row-paid']")!.TextContent.Trim());
        Assert.Equal("Housing · Budget_MethodAccount", rows[0].QuerySelector("[data-testid='exp-row-sub']")!.TextContent.Trim());
        Assert.Equal("$13.00", rows[1].QuerySelector("[data-testid='exp-row-budget']")!.TextContent.Trim());
        Assert.Equal("Budget_MethodCard", rows[1].QuerySelector("[data-testid='exp-row-paid']")!.TextContent.Trim());

        Assert.Null(rows[0].GetAttribute("data-inactive"));
        Assert.Equal("true", rows[2].GetAttribute("data-inactive"));
        Assert.Null(rows[2].QuerySelector("[data-testid='exp-handle']")); // nothing to reorder
        Assert.Contains("Budget_StatusInactive", rows[2].TextContent);
        Assert.NotNull(rows[2].QuerySelector("[data-testid='exp-edit']")); // still editable, which is how it comes back

        Assert.Equal("₡306,500.00", cut.Find("[data-testid='exp-fixed'] [data-testid='exp-total']").TextContent.Trim());
        Assert.Contains("Budget_FixedHint", cut.Find("[data-testid='exp-fixed']").TextContent);
        Assert.Contains("include_inactive=true", Assert.Single(Http.Requests, r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/expenses/fixed").RequestUri!.Query);
    }

    // ---------------------------------------------------------------- reorder: the move menu and drag

    [Fact]
    public async Task Handle_OpensAMoveMenu_MoveDownPutsTheActiveOrder_WithoutTheInactiveLine()
    {
        await SignInAsync();
        StubPage();
        Http.On(HttpMethod.Put, "/api/expenses/fixed/order", "", HttpStatusCode.NoContent);
        var cut = RenderPage();

        Assert.Empty(cut.FindAll("[data-testid='exp-move-menu']"));
        var handle = cut.FindAll("[data-testid='exp-fixed'] [data-testid='exp-handle']")[0];
        Assert.Contains("Mortgage", handle.GetAttribute("aria-label")); // "Reorder Mortgage" — the handle names its line
        handle.Click();

        var menu = cut.Find("[data-testid='exp-move-menu']");
        Assert.True(menu.QuerySelector("[data-testid='exp-move-up']")!.HasAttribute("disabled"));    // first active can't move up
        Assert.False(menu.QuerySelector("[data-testid='exp-move-down']")!.HasAttribute("disabled"));
        Assert.Equal(2, menu.QuerySelectorAll("[data-testid='exp-move-to'] option").Length);          // positions 1..2: the active lines only
        cut.Find("[data-testid='exp-move-down']").Click();

        cut.WaitForAssertion(() => Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put));
        var body = await Http.Requests.Single(r => r.Method == HttpMethod.Put).Content!.ReadAsStringAsync();
        Assert.Contains($"[\"{L2}\",\"{L1}\"]", body); // swapped, inactive L3 excluded
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='exp-move-menu']"))); // the menu closes on a move
        // Optimistic: the rows swap on screen before the reload answers.
        Assert.Equal("Netflix", cut.FindAll("[data-testid='exp-fixed'] [data-testid='exp-row-name']")[0].TextContent.Trim());
    }

    [Fact]
    public async Task MoveToPosition_PutsTheOrder_AndAFailedSaveRollsBackWithTheError()
    {
        await SignInAsync();
        StubPage();
        Http.On(HttpMethod.Put, "/api/expenses/fixed/order", """{"error":"invalid_request","message":"stale"}""", HttpStatusCode.BadRequest);
        var cut = RenderPage();

        cut.FindAll("[data-testid='exp-fixed'] [data-testid='exp-handle']")[0].Click();
        cut.Find("[data-testid='exp-move-to']").Change("2");

        cut.WaitForAssertion(() => Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put));
        Assert.Contains($"[\"{L2}\",\"{L1}\"]", await Http.Requests.Single(r => r.Method == HttpMethod.Put).Content!.ReadAsStringAsync());
        cut.WaitForAssertion(() => Assert.Contains("Budget_ReorderError", cut.Find("[data-testid='exp-fixed'] [data-testid='exp-error']").TextContent));
        Assert.Equal("Mortgage", cut.FindAll("[data-testid='exp-fixed'] [data-testid='exp-row-name']")[0].TextContent.Trim()); // rolled back
    }

    [Fact]
    public async Task Drag_DroppingARowOnAnother_PutsTheNewOrder()
    {
        await SignInAsync();
        StubPage();
        Http.On(HttpMethod.Put, "/api/expenses/fixed/order", "", HttpStatusCode.NoContent);
        var cut = RenderPage();

        var rows = cut.FindAll("[data-testid='exp-fixed'] [data-testid='exp-row']");
        Assert.Equal("true", rows[0].GetAttribute("draggable"));
        Assert.Null(rows[2].GetAttribute("draggable")); // inactive lines are not drag sources
        rows[0].DragStart();
        cut.FindAll("[data-testid='exp-fixed'] [data-testid='exp-row']")[1].Drop(); // re-found: picking a row up re-renders

        cut.WaitForAssertion(() => Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put));
        Assert.Contains($"[\"{L2}\",\"{L1}\"]", await Http.Requests.Single(r => r.Method == HttpMethod.Put).Content!.ReadAsStringAsync());
    }

    // ---------------------------------------------------------------- the dialog

    [Fact]
    public async Task AddALine_OpensTheDialog_TheKindSwitchPicksTheList_AndCreatePostsTheSingleCurrencyPayload()
    {
        await SignInAsync();
        StubPage();
        Http.On(HttpMethod.Post, "/api/expenses/variable", $$"""{"id":"{{L3}}","name":"Water","budget_crc":0,"budget_usd":25,"payment_method":"bank_account","category_id":"{{Cat2}}","sort_order":3,"is_active":true}""", HttpStatusCode.Created);
        var cut = RenderPage();

        Assert.Empty(cut.FindAll("[data-testid='budget-dialog']"));
        Assert.Empty(cut.FindAll("[data-testid='exp-new']")); // one button for both lists, in the page head
        cut.Find("[data-testid='budget-add']").Click();
        var dialog = cut.Find("[data-testid='budget-dialog']");
        Assert.Equal("dialog", dialog.GetAttribute("role"));
        Assert.True(cut.Find("[data-testid='budget-dialog-kind-fixed']").HasAttribute("checked"));
        cut.Find("[data-testid='budget-dialog-kind-variable']").Change(true);

        cut.Find("[data-testid='exp-name']").Input("Water");
        cut.Find("[data-testid='exp-amount']").Change("25");
        cut.Find("[data-testid='exp-currency']").Change("USD");
        cut.Find("[data-testid='exp-category']").Change(Cat2);
        cut.Find("[data-testid='exp-method']").Change("bank_account");
        cut.Find("[data-testid='exp-save']").Click();

        cut.WaitForElement("[data-testid='budget-notice']");
        var post = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/expenses/variable");
        var body = await post.Content!.ReadAsStringAsync();
        Assert.Contains("\"budget_crc\":0", body);
        Assert.Contains("\"budget_usd\":25", body);
        Assert.Contains("\"payment_method\":\"bank_account\"", body);
        Assert.Contains($"\"category_id\":\"{Cat2}\"", body);
        Assert.DoesNotContain("bank_id", body); // a plan names no bank
        Assert.Empty(cut.FindAll("[data-testid='exp-bank']"));
        Assert.Empty(cut.FindAll("[data-testid='budget-dialog']")); // closed on save
        Assert.Equal(2, Http.Requests.Count(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/expenses/variable")); // that list reloaded
    }

    [Fact]
    public async Task Dialog_EscapeAndCancelClose_WithoutARequest()
    {
        await SignInAsync();
        StubPage();
        var cut = RenderPage();

        cut.Find("[data-testid='budget-add']").Click();
        cut.Find("[data-testid='exp-name']").Input("Nope");
        cut.Find("[data-testid='budget-dialog']").KeyDown(Key.Escape);
        Assert.Empty(cut.FindAll("[data-testid='budget-dialog']"));

        cut.Find("[data-testid='budget-add']").Click();
        cut.Find("[data-testid='exp-cancel']").Click();
        Assert.Empty(cut.FindAll("[data-testid='budget-dialog']"));
        Assert.DoesNotContain(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith("/api/expenses"));
    }

    [Fact]
    public async Task Edit_OpensTheDialogPrefilled_WithoutAKindSwitch_AndPutsToTheLinesList()
    {
        await SignInAsync();
        StubPage();
        Http.On(HttpMethod.Put, $"/api/expenses/fixed/{L2}", $$"""{"id":"{{L2}}","name":"Netflix","budget_crc":0,"budget_usd":15,"payment_method":"credit_card","category_id":"{{Cat2}}","sort_order":1,"is_active":false}""");
        var cut = RenderPage();

        cut.FindAll("[data-testid='exp-fixed'] [data-testid='exp-edit']")[1].Click();
        Assert.Equal("Netflix", cut.Find("[data-testid='exp-name']").GetAttribute("value"));
        Assert.Equal("13", cut.Find("[data-testid='exp-amount']").GetAttribute("value"));
        Assert.Equal("USD", cut.Find("[data-testid='exp-currency']").GetAttribute("value"));
        Assert.Empty(cut.FindAll("[data-testid='budget-dialog-kind']")); // a line does not change lists
        cut.Find("[data-testid='exp-amount']").Change("15");
        cut.Find("[data-testid='exp-active']").Change(false);
        cut.Find("[data-testid='exp-save']").Click();

        cut.WaitForElement("[data-testid='budget-notice']");
        var put = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put);
        var body = await put.Content!.ReadAsStringAsync();
        Assert.Contains("\"budget_usd\":15", body);
        Assert.Contains("\"is_active\":false", body);
    }

    [Fact]
    public async Task Dialog_CanCreateTheCategoryInline_AndTheLineSavesWithIt()
    {
        const string NewCatId = "bbbbbbbb-0000-0000-0000-000000000009";
        await SignInAsync();
        StubPage();
        Http.On(HttpMethod.Post, "/api/categories", $$"""{"id":"{{NewCatId}}","name":"Viajes","is_active":true}""", HttpStatusCode.Created);
        Http.On(HttpMethod.Post, "/api/expenses/fixed", $$"""{"id":"{{L3}}","name":"Hotel","budget_crc":80000,"budget_usd":0,"payment_method":"credit_card","category_id":"{{NewCatId}}","sort_order":3,"is_active":true}""", HttpStatusCode.Created);
        var cut = RenderPage();

        cut.Find("[data-testid='budget-add']").Click();
        cut.Find("[data-testid='exp-category-new']").Click();
        cut.Find("[data-testid='exp-category-new-name']").Input("Viajes");
        cut.Find("[data-testid='exp-category-new-save']").Click();

        cut.WaitForAssertion(() => Assert.Equal(NewCatId, cut.Find("[data-testid='exp-category']").GetAttribute("value")));
        cut.Find("[data-testid='exp-name']").Input("Hotel");
        cut.Find("[data-testid='exp-amount']").Change("80000");
        cut.Find("[data-testid='exp-save']").Click();

        cut.WaitForElement("[data-testid='budget-notice']");
        var post = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/expenses/fixed");
        Assert.Contains($"\"category_id\":\"{NewCatId}\"", await post.Content!.ReadAsStringAsync());
    }

    [Fact]
    public async Task InactiveClash_InTheDialog_ReactivateRestoresTheStoredName()
    {
        await SignInAsync();
        StubPage();
        Http.On(HttpMethod.Post, "/api/expenses/fixed", $$"""{"error":"expense_exists_inactive","message":"'Old' already exists but is inactive — reactivate it?","existing_id":"{{L3}}","existing_name":"Old"}""", HttpStatusCode.Conflict);
        Http.On(HttpMethod.Put, $"/api/expenses/fixed/{L3}", $$"""{"id":"{{L3}}","name":"Old","budget_crc":9000,"budget_usd":0,"payment_method":"credit_card","category_id":"{{Cat2}}","sort_order":2,"is_active":true}""");
        var cut = RenderPage();

        cut.Find("[data-testid='budget-add']").Click();
        cut.Find("[data-testid='exp-name']").Input("old");
        cut.Find("[data-testid='exp-amount']").Change("9000");
        cut.Find("[data-testid='exp-category']").Change(Cat2);
        cut.Find("[data-testid='exp-save']").Click();
        cut.WaitForElement("[data-testid='budget-dialog'] [data-testid='exp-reactivate']").Click();

        cut.WaitForElement("[data-testid='budget-notice']");
        var body = await Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put).Content!.ReadAsStringAsync();
        Assert.Contains("\"name\":\"Old\"", body);
        Assert.Contains("\"budget_crc\":9000", body);
        Assert.Contains("\"is_active\":true", body);
        Assert.Empty(cut.FindAll("[data-testid='budget-dialog']"));
    }

    [Fact]
    public async Task EmptyLists_ShowTheEmptyState_AndAZeroHeader()
    {
        await SignInAsync();
        StubPage(fixedList: "[]", variableList: "[]");
        var cut = Render<Budget>();

        cut.WaitForElement("[data-testid='exp-fixed'] [data-testid='exp-empty']");
        cut.WaitForElement("[data-testid='exp-variable'] [data-testid='exp-empty']");
        cut.WaitForAssertion(() => Assert.Equal("₡0.00", cut.Find("[data-testid='budget-planned']").TextContent.Trim()));
        Assert.DoesNotContain(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/banks"); // a plan names no bank: the page has no reason to load them
    }
}
