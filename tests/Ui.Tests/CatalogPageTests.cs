using Bunit;
using Microsoft.AspNetCore.Components;
using Vuelto.Shared.Ui.Components;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>CATALOG-1/2 page component: lists, creates, and turns an inactive-name clash into a one-click reactivation.</summary>
public class CatalogPageTests : ComponentTestBase
{
    private const string Food = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string Gym = "bbbbbbbb-0000-0000-0000-000000000002";
    private const string List = $$"""
        [{"id":"{{Food}}","name":"Food","is_active":true},
         {"id":"{{Gym}}","name":"Gym","is_active":false}]
        """;

    private IRenderedComponent<CatalogPage> RenderCategories() => Render<CatalogPage>(p => p
        .Add(c => c.Endpoint, "/api/categories")
        .Add(c => c.TitleKey, "Catalog_Categories_Title")
        .Add(c => c.SubtitleKey, "Catalog_Categories_Subtitle")
        .Add(c => c.NewKey, "Catalog_Categories_New")
        .Add(c => c.LoadErrorKey, "Catalog_Categories_LoadError"));

    [Fact]
    public async Task Lists_ActiveAndInactive_WithBadges()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/categories", List);

        var cut = RenderCategories();

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid='catalog-row']").Count));
        var rows = cut.FindAll("[data-testid='catalog-row']");
        Assert.Contains("Catalog_Active", rows[0].TextContent);
        Assert.Contains("Catalog_Inactive", rows[1].TextContent);
        Assert.Contains("include_inactive=true", Assert.Single(Http.Requests, r => r.Method == HttpMethod.Get).RequestUri!.Query);
    }

    [Fact]
    public async Task Create_PostsTheName_AndReloads()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/categories", List);
        Http.On(HttpMethod.Post, "/api/categories", $$"""{"id":"{{Food}}","name":"Viajes","is_active":true}""", System.Net.HttpStatusCode.Created);

        var cut = RenderCategories();
        cut.WaitForElement("[data-testid='catalog-new']").Click();
        cut.Find("[data-testid='catalog-name']").Input("Viajes");
        cut.Find("[data-testid='catalog-save']").Click();

        cut.WaitForElement("[data-testid='catalog-notice']");
        // SignInAsync itself POSTs /api/auth/refresh — filter by path, not just by method.
        var post = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/categories");
        Assert.Contains("\"name\":\"Viajes\"", await post.Content!.ReadAsStringAsync());
        Assert.Empty(cut.FindAll("[data-testid='catalog-form']"));               // form closed
        Assert.Equal(2, Http.Requests.Count(r => r.Method == HttpMethod.Get));  // reloaded
    }

    [Fact]
    public async Task InactiveClash_ShowsReactivate_WhichPutsIsActiveTrue()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/categories", List);
        Http.On(HttpMethod.Post, "/api/categories",
            $$"""{"error":"category_exists_inactive","message":"'Gym' already exists but is inactive — reactivate it?","existing_id":"{{Gym}}","existing_name":"Gym"}""",
            System.Net.HttpStatusCode.Conflict);
        Http.On(HttpMethod.Put, $"/api/categories/{Gym}", $$"""{"id":"{{Gym}}","name":"Gym","is_active":true}""");

        var cut = RenderCategories();
        cut.WaitForElement("[data-testid='catalog-new']").Click();
        cut.Find("[data-testid='catalog-name']").Input("gym");
        cut.Find("[data-testid='catalog-save']").Click();

        cut.WaitForElement("[data-testid='catalog-reactivate']").Click();

        cut.WaitForElement("[data-testid='catalog-notice']");
        var put = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put);
        Assert.EndsWith($"/api/categories/{Gym}", put.RequestUri!.AbsolutePath);
        var body = await put.Content!.ReadAsStringAsync();
        Assert.Contains("\"is_active\":true", body);
        Assert.Contains("\"name\":\"Gym\"", body); // the stored name is restored, not the typed "gym"
    }

    [Fact]
    public async Task ActiveClash_ShowsTheMessage_NoReactivateButton()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/categories", List);
        Http.On(HttpMethod.Post, "/api/categories",
            """{"error":"category_exists","message":"A category named 'Food' already exists","existing_id":null,"existing_name":null}""",
            System.Net.HttpStatusCode.Conflict);

        var cut = RenderCategories();
        cut.WaitForElement("[data-testid='catalog-new']").Click();
        cut.Find("[data-testid='catalog-name']").Input("food");
        cut.Find("[data-testid='catalog-save']").Click();

        Assert.Contains("already exists", cut.WaitForElement("[data-testid='catalog-form-error']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='catalog-reactivate']"));
    }

    [Fact]
    public async Task BlankName_IsRejectedLocally_WithoutARequest()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/categories", List);

        var cut = RenderCategories();
        cut.WaitForElement("[data-testid='catalog-new']").Click();
        cut.Find("[data-testid='catalog-save']").Click();

        Assert.Contains("Catalog_NameRequired", cut.WaitForElement("[data-testid='catalog-form-error']").TextContent);
        Assert.DoesNotContain(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/categories" && r.Method == HttpMethod.Post);
    }
}
