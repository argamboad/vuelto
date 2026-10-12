using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Shared.Ui;
using Vuelto.Shared.Ui.Components;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>EMAIL-6 UI: the header's Review link carries the pending count (hidden at zero or when the count can't be read), and the dashboard shows the review banner even before the first month exists.</summary>
public class ReviewBadgeTests : ComponentTestBase
{
    [Fact]
    public async Task Header_ShowsTheReviewCount_WhenDraftsAreWaiting()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/pending-vouchers/count", """{"count":3}""");

        var cut = Render<AppHeader>();

        cut.WaitForAssertion(() => Assert.Equal("3", cut.Find("[data-testid='nav-review-count']").TextContent.Trim()));
        Assert.Equal("/review", cut.Find("[data-testid='nav-review']").GetAttribute("href"));
    }

    [Fact]
    public async Task Header_HidesTheCount_AtZero_OrWhenTheApiIsUnavailable()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/pending-vouchers/count", """{"count":0}""");
        var zero = Render<AppHeader>();
        zero.WaitForElement("[data-testid='nav-review']");
        Assert.Empty(zero.FindAll("[data-testid='nav-review-count']"));

        Http.On(HttpMethod.Get, "/api/pending-vouchers/count", """{"error":"boom"}""", System.Net.HttpStatusCode.InternalServerError);
        var failing = Render<AppHeader>();
        failing.WaitForElement("[data-testid='nav-review']");
        Assert.Empty(failing.FindAll("[data-testid='nav-review-count']"));
    }

    [Fact]
    public async Task Header_RecountsAtOnce_WhenTheQueueChanges()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/pending-vouchers/count", """{"count":2}""");
        var cut = Render<AppHeader>();
        cut.WaitForAssertion(() => Assert.Equal("2", cut.Find("[data-testid='nav-review-count']").TextContent.Trim()));

        Http.On(HttpMethod.Get, "/api/pending-vouchers/count", """{"count":0}""");
        Services.GetRequiredService<ReviewQueueNotifier>().NotifyChanged(); // what the Review page raises after a confirm/discard

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='nav-review-count']")));
    }

    [Fact]
    public async Task Dashboard_ShowsTheReviewBanner_EvenWithNoMonths()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/pending-vouchers/count", """{"count":2}""");
        Http.On(HttpMethod.Get, "/api/months", "[]");

        var cut = Render<Dashboard>();

        cut.WaitForElement("[data-testid='dash-empty']");
        Assert.Contains("Dash_ReviewBanner[2]", cut.Find("[data-testid='dash-review-banner']").TextContent);
        Assert.Equal("/review", cut.Find("[data-testid='dash-review-link']").GetAttribute("href"));
    }
}
