using System.Net;
using Bunit;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>EMAIL-5 UI: lists the household's rules with category names, add posts the rule (409 explains), edit puts it, delete is two-step, and the empty state shows.</summary>
public class MerchantMappingsPageTests : ComponentTestBase
{
    private const string R1 = "eeeeeeee-1111-0000-0000-000000000001";
    private const string Cat1 = "cccccccc-0000-0000-0000-000000000001";
    private const string Cat2 = "cccccccc-0000-0000-0000-000000000002";
    private const string Categories = $$"""[{"id":"{{Cat1}}","name":"Dining","is_active":true},{"id":"{{Cat2}}","name":"Groceries","is_active":true}]""";
    private const string Rules = $$"""[{"id":"{{R1}}","merchant_pattern":"Taco Bell","category_id":"{{Cat1}}","category_name":"Dining","suggested_class":"extraordinary"}]""";

    [Fact]
    public async Task Lists_Rules_WithCategoryAndClass_OrTheEmptyState()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/categories", Categories);
        Http.On(HttpMethod.Get, "/api/merchant-mappings", Rules);

        var cut = Render<MerchantMappings>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='mapping-row']")));
        var row = cut.Find("[data-testid='mapping-row']");
        Assert.Contains("Taco Bell", row.TextContent); Assert.Contains("Dining", row.TextContent); Assert.Contains("Tx_Extraordinary", row.TextContent);

        Http.On(HttpMethod.Get, "/api/merchant-mappings", "[]");
        var empty = Render<MerchantMappings>();
        empty.WaitForElement("[data-testid='mapping-empty']");
    }

    [Fact]
    public async Task Add_PostsTheRule_AndAConflictExplains()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/categories", Categories);
        Http.On(HttpMethod.Get, "/api/merchant-mappings", "[]");
        Http.On(HttpMethod.Post, "/api/merchant-mappings", $$"""{"id":"{{R1}}","merchant_pattern":"AUTOMERCADO","category_id":"{{Cat2}}","category_name":"Groceries","suggested_class":null}""", HttpStatusCode.Created);

        var cut = Render<MerchantMappings>();
        cut.WaitForElement("[data-testid='mapping-empty']");
        cut.Find("[data-testid='mapping-save']").Click();
        cut.WaitForAssertion(() => Assert.Contains("Mapping_Required", cut.Find("[data-testid='mapping-notice']").TextContent));
        Assert.DoesNotContain(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/merchant-mappings");

        cut.Find("[data-testid='mapping-pattern']").Change(" AUTOMERCADO ");
        cut.Find("[data-testid='mapping-category']").Change(Cat2);
        cut.Find("[data-testid='mapping-save']").Click();

        cut.WaitForAssertion(() => Assert.Contains("Mapping_Saved", cut.Find("[data-testid='mapping-notice']").TextContent));
        var body = await Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/merchant-mappings").Content!.ReadAsStringAsync();
        Assert.Contains("\"merchant_pattern\":\"AUTOMERCADO\"", body);
        Assert.Contains($"\"category_id\":\"{Cat2}\"", body);
        Assert.Contains("\"suggested_class\":null", body);

        Http.On(HttpMethod.Post, "/api/merchant-mappings", """{"error":"mapping_exists","message":"x"}""", HttpStatusCode.Conflict);
        cut.Find("[data-testid='mapping-pattern']").Change("automercado");
        cut.Find("[data-testid='mapping-category']").Change(Cat1);
        cut.Find("[data-testid='mapping-save']").Click();
        cut.WaitForAssertion(() => Assert.Contains("Mapping_Exists", cut.Find("[data-testid='mapping-notice']").TextContent));
    }

    [Fact]
    public async Task Edit_PutsTheRule_AndDelete_IsTwoStep()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/categories", Categories);
        Http.On(HttpMethod.Get, "/api/merchant-mappings", Rules);
        Http.On(HttpMethod.Put, $"/api/merchant-mappings/{R1}", Rules.TrimStart('[').TrimEnd(']'));
        Http.On(HttpMethod.Delete, $"/api/merchant-mappings/{R1}", "", HttpStatusCode.NoContent);

        var cut = Render<MerchantMappings>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='mapping-edit']")));
        cut.Find("[data-testid='mapping-edit']").Click();
        Assert.Equal("Taco Bell", cut.Find("[data-testid='mapping-pattern']").GetAttribute("value"));
        Assert.Equal("extraordinary", cut.Find("[data-testid='mapping-class']").GetAttribute("value"));
        cut.Find("[data-testid='mapping-class']").Change("budgeted");
        cut.Find("[data-testid='mapping-save']").Click();

        cut.WaitForAssertion(() => Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put));
        Assert.Contains("\"suggested_class\":\"budgeted\"", await Http.Requests.Single(r => r.Method == HttpMethod.Put).Content!.ReadAsStringAsync());

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='mapping-delete']")));
        cut.Find("[data-testid='mapping-delete']").Click();
        Assert.DoesNotContain(Http.Requests, r => r.Method == HttpMethod.Delete);
        cut.Find("[data-testid='mapping-delete-confirm']").Click();
        cut.WaitForAssertion(() => Assert.Single(Http.Requests, r => r.Method == HttpMethod.Delete && r.RequestUri!.AbsolutePath == $"/api/merchant-mappings/{R1}"));
        Assert.Contains("Mapping_Deleted", cut.Find("[data-testid='mapping-notice']").TextContent);
    }
}
