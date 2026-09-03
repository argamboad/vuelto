using System.Net;
using Bunit;
using Vuelto.Shared.Ui.Components;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>EXPENSES-1 UI: the section lists lines with resolved names, creates with the single-currency payload, reorders by PUTting the active set, and restores an inactive clash.</summary>
public class BudgetPageTests : ComponentTestBase
{
    private const string Cat1 = "bbbbbbbb-0000-0000-0000-000000000001";
    private const string Cat2 = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string BankId = "cccccccc-0000-0000-0000-000000000003";
    private const string L1 = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string L2 = "aaaaaaaa-0000-0000-0000-000000000002";
    private const string L3 = "aaaaaaaa-0000-0000-0000-000000000003";
    private const string List = $$"""
        [{"id":"{{L1}}","name":"Mortgage","budget_crc":300000,"budget_usd":0,"payment_method":"bank_account","category_id":"{{Cat1}}","bank_id":"{{BankId}}","sort_order":0,"is_active":true},
         {"id":"{{L2}}","name":"Netflix","budget_crc":0,"budget_usd":13,"payment_method":"credit_card","category_id":"{{Cat2}}","bank_id":null,"sort_order":1,"is_active":true},
         {"id":"{{L3}}","name":"Old","budget_crc":5000,"budget_usd":0,"payment_method":"credit_card","category_id":"{{Cat2}}","bank_id":null,"sort_order":2,"is_active":false}]
        """;

    private static readonly List<ExpenseLinesSection.NamedItem> Categories = [new(Guid.Parse(Cat1), "Housing", true), new(Guid.Parse(Cat2), "Entertainment", true)];
    private static readonly List<ExpenseLinesSection.NamedItem> Banks = [new(Guid.Parse(BankId), "BAC", true)];

    private IRenderedComponent<ExpenseLinesSection> RenderFixed() => Render<ExpenseLinesSection>(p => p
        .Add(x => x.Kind, "fixed").Add(x => x.TitleKey, "Budget_Fixed").Add(x => x.NewKey, "Budget_AddFixed")
        .Add(x => x.Categories, Categories).Add(x => x.Banks, Banks));

    [Fact]
    public async Task Lists_WithResolvedNames_BudgetCurrency_AndBadges()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/expenses/fixed", List);

        var cut = RenderFixed();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid='exp-row']").Count));
        var rows = cut.FindAll("[data-testid='exp-row']");
        Assert.Contains("₡300,000.00", rows[0].TextContent); Assert.Contains("Housing", rows[0].TextContent); Assert.Contains("BAC", rows[0].TextContent);
        Assert.Contains("$13.00", rows[1].TextContent); Assert.Contains("Budget_Unassigned", rows[1].TextContent);
        Assert.Contains("Catalog_Inactive", rows[2].TextContent);
        Assert.Contains("include_inactive=true", Assert.Single(Http.Requests, r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/expenses/fixed").RequestUri!.Query);
    }

    [Fact]
    public async Task Create_PostsTheSingleCurrencyPayload_AndReloads()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/expenses/fixed", List);
        Http.On(HttpMethod.Post, "/api/expenses/fixed", $$"""{"id":"{{L3}}","name":"Water","budget_crc":0,"budget_usd":25,"payment_method":"bank_account","category_id":"{{Cat2}}","bank_id":null,"sort_order":3,"is_active":true}""", HttpStatusCode.Created);

        var cut = RenderFixed();
        cut.WaitForElement("[data-testid='exp-new']").Click();
        cut.Find("[data-testid='exp-name']").Input("Water");
        cut.Find("[data-testid='exp-amount']").Change("25");
        cut.Find("[data-testid='exp-currency']").Change("USD");
        cut.Find("[data-testid='exp-category']").Change(Cat2);
        cut.Find("[data-testid='exp-method']").Change("bank_account");
        cut.Find("[data-testid='exp-save']").Click();

        cut.WaitForElement("[data-testid='exp-notice']");
        // SignInAsync itself POSTs /api/auth/refresh — filter by path, not just by method.
        var post = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/expenses/fixed");
        var body = await post.Content!.ReadAsStringAsync();
        Assert.Contains("\"budget_crc\":0", body);
        Assert.Contains("\"budget_usd\":25", body);
        Assert.Contains("\"payment_method\":\"bank_account\"", body);
        Assert.Contains($"\"category_id\":\"{Cat2}\"", body);
        Assert.Contains("\"bank_id\":null", body);
        Assert.Equal(2, Http.Requests.Count(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/expenses/fixed"));
    }

    [Fact]
    public async Task MoveDown_PutsTheActiveOrder_WithoutTheInactiveLine()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/expenses/fixed", List);
        Http.On(HttpMethod.Put, "/api/expenses/fixed/order", "", HttpStatusCode.NoContent);

        var cut = RenderFixed();
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid='exp-row']").Count));
        Assert.True(cut.FindAll("[data-testid='exp-up']")[0].HasAttribute("disabled"));   // first active can't move up
        Assert.True(cut.FindAll("[data-testid='exp-down']")[1].HasAttribute("disabled")); // last active can't move down

        cut.FindAll("[data-testid='exp-down']")[0].Click();

        cut.WaitForAssertion(() => Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put));
        var body = await Http.Requests.Single(r => r.Method == HttpMethod.Put).Content!.ReadAsStringAsync();
        Assert.Contains($"[\"{L2}\",\"{L1}\"]", body); // swapped, inactive L3 excluded
    }

    [Fact]
    public async Task InactiveClash_Reactivate_RestoresTheStoredName()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/expenses/fixed", List);
        Http.On(HttpMethod.Post, "/api/expenses/fixed", $$"""{"error":"expense_exists_inactive","message":"'Old' already exists but is inactive — reactivate it?","existing_id":"{{L3}}","existing_name":"Old"}""", HttpStatusCode.Conflict);
        Http.On(HttpMethod.Put, $"/api/expenses/fixed/{L3}", $$"""{"id":"{{L3}}","name":"Old","budget_crc":9000,"budget_usd":0,"payment_method":"credit_card","category_id":"{{Cat2}}","bank_id":null,"sort_order":2,"is_active":true}""");

        var cut = RenderFixed();
        cut.WaitForElement("[data-testid='exp-new']").Click();
        cut.Find("[data-testid='exp-name']").Input("old");
        cut.Find("[data-testid='exp-amount']").Change("9000");
        cut.Find("[data-testid='exp-category']").Change(Cat2);
        cut.Find("[data-testid='exp-save']").Click();
        cut.WaitForElement("[data-testid='exp-reactivate']").Click();

        cut.WaitForElement("[data-testid='exp-notice']");
        var body = await Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put).Content!.ReadAsStringAsync();
        Assert.Contains("\"name\":\"Old\"", body);
        Assert.Contains("\"budget_crc\":9000", body);
        Assert.Contains("\"is_active\":true", body);
    }

    [Fact]
    public async Task BudgetPage_LoadsCatalogs_AndRendersBothSections()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/categories", $$"""[{"id":"{{Cat1}}","name":"Housing","is_active":true}]""");
        Http.On(HttpMethod.Get, "/api/banks", $$"""[{"id":"{{BankId}}","name":"BAC","is_active":true}]""");
        Http.On(HttpMethod.Get, "/api/expenses/fixed", List);
        Http.On(HttpMethod.Get, "/api/expenses/variable", "[]");

        var cut = Render<Budget>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid='exp-fixed'] [data-testid='exp-row']").Count));
        cut.WaitForElement("[data-testid='exp-variable'] [data-testid='exp-empty']");
        Assert.Contains("include_inactive=true", Assert.Single(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/banks").RequestUri!.Query); // all states for names
    }
}
