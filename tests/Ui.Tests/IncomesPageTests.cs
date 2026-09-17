using System.Net;
using Bunit;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// INCOME-1 page: lists the lines with their member, pay period and review badge; creates with the wire payload (pay
/// days only for biweekly); refuses a zero amount and equal pay days locally; turns an inactive clash into a restore;
/// reorders the active lines; and names a member who has left instead of dropping the line's member.
/// </summary>
public class IncomesPageTests : ComponentTestBase
{
    private const string Salary = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Son = "aaaaaaaa-0000-0000-0000-000000000002";
    private const string Old = "aaaaaaaa-0000-0000-0000-000000000003";
    private const string Allan = "bbbbbbbb-0000-0000-0000-000000000001";
    private const string Departed = "bbbbbbbb-0000-0000-0000-000000000009";

    private const string List = $$"""
        [{"id":"{{Salary}}","name":"Allan salary","member_user_id":"{{Allan}}","currency":"USD","kind":"fixed","pay_period":"weekly","amount":500,"pay_days":null,"sort_order":0,"is_active":true,"needs_review":true},
         {"id":"{{Son}}","name":"Son","member_user_id":"{{Departed}}","currency":"CRC","kind":"variable","pay_period":"biweekly","amount":300000,"pay_days":[15,31],"sort_order":1,"is_active":true,"needs_review":false},
         {"id":"{{Old}}","name":"Old job","member_user_id":null,"currency":"USD","kind":"fixed","pay_period":"monthly","amount":900,"pay_days":null,"sort_order":2,"is_active":false,"needs_review":false}]
        """;

    private const string Household = $$"""
        {"id":"cccccccc-0000-0000-0000-000000000001","name":"Casa","members":[{"user_id":"{{Allan}}","display_name":"Allan","email":"allan@example.com","role":"owner"}]}
        """;

    private async Task<IRenderedComponent<Incomes>> RenderSignedIn(string list = List)
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/incomes", list);
        Http.On(HttpMethod.Get, "/api/household", Household);
        var cut = Render<Incomes>();
        return cut;
    }

    [Fact]
    public async Task Lists_MemberPeriodStatusAndTheReviewBadge()
    {
        var cut = await RenderSignedIn();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid='inc-row']").Count));
        var rows = cut.FindAll("[data-testid='inc-row']");
        Assert.Contains("$500.00", rows[0].TextContent);
        Assert.Contains("Allan", rows[0].QuerySelector("[data-testid='inc-row-sub']")!.TextContent);
        Assert.Equal("Allan", rows[0].QuerySelector("[data-testid='inc-row-member']")!.TextContent.Trim());
        Assert.Contains("Income_KindFixed", rows[0].QuerySelector("[data-testid='inc-row-period']")!.TextContent);
        Assert.Contains("Income_PeriodWeekly", rows[0].TextContent);
        Assert.NotNull(rows[0].QuerySelector("[data-testid='inc-row-review']"));
        Assert.Contains("₡300,000.00", rows[1].TextContent);
        Assert.Contains("Income_PeriodBiweekly (15, Income_LastDay)", rows[1].QuerySelector("[data-testid='inc-row-period']")!.TextContent);
        Assert.Contains("Income_MemberLeft", rows[1].TextContent);
        Assert.Contains("Income_KindVariable", rows[1].TextContent);
        Assert.Null(rows[1].QuerySelector("[data-testid='inc-row-review']"));
        Assert.Contains("Catalog_Inactive", rows[2].TextContent);
        Assert.Contains("Income_MemberHousehold", rows[2].TextContent);
        Assert.Empty(rows[2].QuerySelectorAll("[data-testid='inc-up']")); // only active lines are ordered
        Assert.Contains("include_inactive=true", Assert.Single(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/incomes").RequestUri!.Query);
    }

    [Fact]
    public async Task Empty_ShowsTheEmptyState()
    {
        var cut = await RenderSignedIn("[]");

        Assert.Contains("Income_Empty", cut.WaitForElement("[data-testid='inc-empty']").TextContent);
    }

    [Fact]
    public async Task Create_Biweekly_PostsTheWirePayloadWithPayDays_AndReloads()
    {
        var cut = await RenderSignedIn();
        Http.On(HttpMethod.Post, "/api/incomes", """{"id":"dddddddd-0000-0000-0000-000000000004","name":"Consulting"}""", HttpStatusCode.Created);

        cut.WaitForElement("[data-testid='inc-new']").Click();
        Assert.Empty(cut.FindAll("[data-testid='inc-day1']")); // monthly by default: no pay days
        Assert.Empty(cut.FindAll("[data-testid='inc-member-hint']")); // a new line has no months yet
        cut.Find("[data-testid='inc-name']").Input("Consulting");
        cut.Find("[data-testid='inc-member']").Change(Allan);
        cut.Find("[data-testid='inc-kind']").Change("variable");
        cut.Find("[data-testid='inc-period']").Change("biweekly");
        cut.Find("[data-testid='inc-day1']").Change("10");
        cut.Find("[data-testid='inc-amount']").Change("750.5");
        cut.Find("[data-testid='inc-currency']").Change("CRC");
        Assert.Contains("Income_HintBiweekly", cut.Find("[data-testid='inc-hint']").TextContent);
        Assert.Contains("Income_KindHint", cut.Find("[data-testid='inc-hint']").TextContent);
        cut.Find("[data-testid='inc-save']").Click();

        cut.WaitForElement("[data-testid='inc-notice']");
        var post = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/incomes");
        Assert.Equal(
            $$"""{"name":"Consulting","member_user_id":"{{Allan}}","currency":"CRC","kind":"variable","pay_period":"biweekly","amount":750.5,"pay_days":[10,31],"is_active":true}""",
            await post.Content!.ReadAsStringAsync());
        Assert.Empty(cut.FindAll("[data-testid='inc-form']"));
        Assert.Equal(2, Http.Requests.Count(r => r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == "/api/incomes"));
    }

    [Fact]
    public async Task Create_Monthly_SendsNoPayDaysAndNoMember()
    {
        var cut = await RenderSignedIn();
        Http.On(HttpMethod.Post, "/api/incomes", "{}", HttpStatusCode.Created);

        cut.WaitForElement("[data-testid='inc-new']").Click();
        cut.Find("[data-testid='inc-name']").Input("Rent from the cabin");
        cut.Find("[data-testid='inc-amount']").Change("400");
        cut.Find("[data-testid='inc-save']").Click();

        cut.WaitForElement("[data-testid='inc-notice']");
        var body = await Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/incomes").Content!.ReadAsStringAsync();
        Assert.Contains("\"member_user_id\":null", body);
        Assert.Contains("\"pay_period\":\"monthly\"", body);
        Assert.Contains("\"pay_days\":null", body);
    }

    [Theory]
    [InlineData("0", "monthly", "Income_AmountRequired")]
    [InlineData("100", "biweekly", "Income_PayDaysDifferent")]
    public async Task InvalidInput_IsRejectedLocally_WithoutARequest(string amount, string period, string message)
    {
        var cut = await RenderSignedIn();

        cut.WaitForElement("[data-testid='inc-new']").Click();
        cut.Find("[data-testid='inc-name']").Input("Bonus");
        cut.Find("[data-testid='inc-amount']").Change(amount);
        cut.Find("[data-testid='inc-period']").Change(period);
        if (period == "biweekly") cut.Find("[data-testid='inc-day1']").Change("31");
        cut.Find("[data-testid='inc-save']").Click();

        Assert.Contains(message, cut.WaitForElement("[data-testid='inc-form-error']").TextContent);
        Assert.DoesNotContain(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/incomes");
    }

    [Fact]
    public async Task ServerValidation_ShowsItsMessage()
    {
        var cut = await RenderSignedIn();
        Http.On(HttpMethod.Post, "/api/incomes", """{"error":"invalid_request","message":"member_user_id must be a member of the household."}""", HttpStatusCode.BadRequest);

        cut.WaitForElement("[data-testid='inc-new']").Click();
        cut.Find("[data-testid='inc-name']").Input("Bonus");
        cut.Find("[data-testid='inc-amount']").Change("5");
        cut.Find("[data-testid='inc-save']").Click();

        Assert.Contains("must be a member", cut.WaitForElement("[data-testid='inc-form-error']").TextContent);
    }

    [Fact]
    public async Task Edit_KeepsADepartedMember_AndPutsEveryField()
    {
        var cut = await RenderSignedIn();
        Http.On(HttpMethod.Put, $"/api/incomes/{Son}", "{}");

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("[data-testid='inc-edit']").Count));
        cut.FindAll("[data-testid='inc-edit']")[1].Click();
        Assert.Equal(Departed, cut.Find("[data-testid='inc-member']").GetAttribute("value"));
        Assert.Contains("Income_MemberFollowsHint", cut.Find("[data-testid='inc-member-hint']").TextContent);
        Assert.Contains("Income_MemberLeft", cut.Find("[data-testid='inc-member']").TextContent);
        Assert.Equal("31", cut.Find("[data-testid='inc-day2']").GetAttribute("value"));
        cut.Find("[data-testid='inc-amount']").Change("320000");
        cut.Find("[data-testid='inc-active']").Change(false);
        cut.Find("[data-testid='inc-save']").Click();

        cut.WaitForElement("[data-testid='inc-notice']");
        var body = await Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put).Content!.ReadAsStringAsync();
        Assert.Equal(
            $$"""{"name":"Son","member_user_id":"{{Departed}}","currency":"CRC","kind":"variable","pay_period":"biweekly","amount":320000,"pay_days":[15,31],"is_active":false}""",
            body);
    }

    [Fact]
    public async Task InactiveClash_Reactivate_RestoresTheStoredName_WithTheTypedDetails()
    {
        var cut = await RenderSignedIn();
        Http.On(HttpMethod.Post, "/api/incomes",
            $$"""{"error":"income_exists_inactive","message":"'Old job' already exists but is inactive — reactivate it?","existing_id":"{{Old}}","existing_name":"Old job"}""",
            HttpStatusCode.Conflict);
        Http.On(HttpMethod.Put, $"/api/incomes/{Old}", "{}");

        cut.WaitForElement("[data-testid='inc-new']").Click();
        cut.Find("[data-testid='inc-name']").Input("old JOB");
        cut.Find("[data-testid='inc-amount']").Change("1000");
        cut.Find("[data-testid='inc-save']").Click();
        cut.WaitForElement("[data-testid='inc-reactivate']").Click();

        cut.WaitForElement("[data-testid='inc-notice']");
        var body = await Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put).Content!.ReadAsStringAsync();
        Assert.Contains("\"name\":\"Old job\"", body);
        Assert.Contains("\"amount\":1000", body);
        Assert.Contains("\"is_active\":true", body);
    }

    [Fact]
    public async Task ActiveClash_ShowsTheMessage_NoReactivateButton()
    {
        var cut = await RenderSignedIn();
        Http.On(HttpMethod.Post, "/api/incomes",
            """{"error":"income_exists","message":"An income named 'Son' already exists","existing_id":null,"existing_name":null}""",
            HttpStatusCode.Conflict);

        cut.WaitForElement("[data-testid='inc-new']").Click();
        cut.Find("[data-testid='inc-name']").Input("son");
        cut.Find("[data-testid='inc-amount']").Change("1");
        cut.Find("[data-testid='inc-save']").Click();

        Assert.Contains("already exists", cut.WaitForElement("[data-testid='inc-form-error']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='inc-reactivate']"));
    }

    [Fact]
    public async Task MoveDown_PutsTheWholeActiveOrder_AndTheEndsCannotMoveOut()
    {
        var cut = await RenderSignedIn();
        Http.On(HttpMethod.Put, "/api/incomes/order", "", HttpStatusCode.NoContent);

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='inc-up']").Count));
        Assert.True(cut.FindAll("[data-testid='inc-up']")[0].HasAttribute("disabled"));
        Assert.True(cut.FindAll("[data-testid='inc-down']")[1].HasAttribute("disabled"));
        cut.FindAll("[data-testid='inc-down']")[0].Click();

        cut.WaitForAssertion(() => Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put));
        var body = await Http.Requests.Single(r => r.Method == HttpMethod.Put).Content!.ReadAsStringAsync();
        Assert.Equal($$"""{"ordered_ids":["{{Son}}","{{Salary}}"]}""", body);
    }

    [Fact]
    public async Task HouseholdUnavailable_StillOffersTheHouseholdOption()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/incomes", "[]");
        var cut = Render<Incomes>(); // /api/household is not stubbed: 404

        cut.WaitForElement("[data-testid='inc-new']").Click();

        var option = Assert.Single(cut.FindAll("[data-testid='inc-member'] option"));
        Assert.Contains("Income_MemberHousehold", option.TextContent);
    }
}
