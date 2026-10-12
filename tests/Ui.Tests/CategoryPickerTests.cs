using System.Net;
using Bunit;
using Vuelto.Shared.Ui.Components;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// The inline category picker: create a category where a transaction is entered — a name is required,
/// a created entry is handed to the page and becomes the selection, an active clash selects the existing
/// entry, an inactive clash offers Reactivate (restoring the stored name), Cancel closes without a call.
/// </summary>
public class CategoryPickerTests : ComponentTestBase
{
    private const string Cat1 = "cccccccc-0000-0000-0000-000000000001";
    private const string NewId = "cccccccc-0000-0000-0000-000000000009";

    private readonly List<CategoryOption> _categories = [new(Guid.Parse(Cat1), "Dining")];
    private readonly List<CategoryOption> _created = [];
    private string? _value;

    private IRenderedComponent<CategoryPicker> RenderPicker() => Render<CategoryPicker>(p => p
        .Add(x => x.TestId, "pick")
        .Add(x => x.Categories, _categories)
        .Add(x => x.Value, _value)
        .Add(x => x.ValueChanged, v => _value = v)
        .Add(x => x.OnCreated, c => _created.Add(c)));

    [Fact]
    public async Task Create_PostsTheName_HandsTheEntryToThePage_AndSelectsIt()
    {
        await SignInAsync();
        Http.On(HttpMethod.Post, "/api/categories", $$"""{"id":"{{NewId}}","name":"Viajes","is_active":true}""", HttpStatusCode.Created);
        var cut = RenderPicker();

        Assert.Empty(cut.FindAll("[data-testid='pick-new-form']"));
        cut.Find("[data-testid='pick-new']").Click();
        cut.Find("[data-testid='pick-new-save']").Click();
        Assert.Contains("Catalog_NameRequired", cut.Find("[data-testid='pick-new-error']").TextContent); // blank refused, no call
        Assert.DoesNotContain(Http.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/api/categories"));

        cut.Find("[data-testid='pick-new-name']").Input("Viajes");
        cut.Find("[data-testid='pick-new-save']").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='pick-new-form']")));
        var post = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/categories");
        Assert.Contains("\"name\":\"Viajes\"", await post.Content!.ReadAsStringAsync());
        Assert.Equal(("Viajes", NewId), (Assert.Single(_created).Name, _value));
    }

    [Fact]
    public async Task ActiveClash_SelectsTheExistingEntry_WithoutAnotherCall()
    {
        await SignInAsync();
        Http.On(HttpMethod.Post, "/api/categories", """{"error":"category_exists","message":"exists","existing_id":null,"existing_name":null}""", HttpStatusCode.Conflict);
        var cut = RenderPicker();

        cut.Find("[data-testid='pick-new']").Click();
        cut.Find("[data-testid='pick-new-name']").Input("dining");
        cut.Find("[data-testid='pick-new-save']").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='pick-new-form']")));
        Assert.Equal(Cat1, _value);
        Assert.Empty(_created); // it was already in the list
        Assert.DoesNotContain(Http.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task InactiveClash_OffersReactivate_ThenRestoresTheStoredName_AndSelectsIt()
    {
        await SignInAsync();
        Http.On(HttpMethod.Post, "/api/categories", $$"""{"error":"category_exists_inactive","message":"inactive","existing_id":"{{NewId}}","existing_name":"Viajes"}""", HttpStatusCode.Conflict);
        Http.On(HttpMethod.Put, $"/api/categories/{NewId}", $$"""{"id":"{{NewId}}","name":"Viajes","is_active":true}""");
        var cut = RenderPicker();

        cut.Find("[data-testid='pick-new']").Click();
        cut.Find("[data-testid='pick-new-name']").Input("VIAJES");
        cut.Find("[data-testid='pick-new-save']").Click();

        var reactivate = cut.WaitForElement("[data-testid='pick-reactivate']");
        Assert.Contains("Category_ReactivateExisting[Viajes]", reactivate.TextContent);
        reactivate.Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='pick-new-form']")));
        var put = Assert.Single(Http.Requests, r => r.Method == HttpMethod.Put);
        var body = await put.Content!.ReadAsStringAsync();
        Assert.Contains("\"name\":\"Viajes\"", body); // the stored name, not the typed casing
        Assert.Contains("\"is_active\":true", body);
        Assert.Equal(("Viajes", NewId), (Assert.Single(_created).Name, _value));
    }

    [Fact]
    public async Task Cancel_ClosesWithoutACall_AndEscapeDoesTheSame()
    {
        await SignInAsync();
        var cut = RenderPicker();

        cut.Find("[data-testid='pick-new']").Click();
        cut.Find("[data-testid='pick-new-name']").Input("x");
        cut.Find("[data-testid='pick-new-cancel']").Click();
        Assert.Empty(cut.FindAll("[data-testid='pick-new-form']"));

        cut.Find("[data-testid='pick-new']").Click();
        cut.Find("[data-testid='pick-new-name']").KeyDown("Escape");
        Assert.Empty(cut.FindAll("[data-testid='pick-new-form']"));
        Assert.DoesNotContain(Http.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/api/categories"));
        Assert.Null(_value);
    }
}
