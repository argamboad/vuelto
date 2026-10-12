using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests.Pages;

/// <summary>
/// The two catalog pages (CATALOG-1/2). Each is a thin shell around <c>CatalogPage</c>, whose behaviour
/// <see cref="CatalogPageTests"/> covers; what only the page decides is which endpoint it lists, and that a
/// signed-out visitor is sent to the login page. The per-page floor (R70) asked for both.
/// </summary>
public class CatalogPagesTests : ComponentTestBase
{
    private const string Rows = """
        [{"id":"aaaaaaaa-0000-0000-0000-000000000001","name":"First","is_active":true},
         {"id":"bbbbbbbb-0000-0000-0000-000000000002","name":"Second","is_active":false}]
        """;

    [Fact]
    public async Task Banks_ListsTheHouseholdsBanks_FromItsOwnEndpoint()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/banks", Rows);

        var page = Render<Banks>();

        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("[data-testid='catalog-row']").Count));
        Assert.Contains("Catalog_Banks_Title", page.Markup);
        Assert.DoesNotContain(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/categories");
    }

    [Fact]
    public async Task Categories_ListsTheHouseholdsCategories_FromItsOwnEndpoint()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/categories", Rows);

        var page = Render<Categories>();

        page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("[data-testid='catalog-row']").Count));
        Assert.Contains("Catalog_Categories_Title", page.Markup);
        Assert.DoesNotContain(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/banks");
    }

    [Fact]
    public void Banks_SignedOut_GoesToLogin()
    {
        Render<Banks>();

        Assert.EndsWith("/login", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public void Categories_SignedOut_GoesToLogin()
    {
        Render<Categories>();

        Assert.EndsWith("/login", Services.GetRequiredService<NavigationManager>().Uri);
    }
}
