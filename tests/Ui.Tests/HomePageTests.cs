using Bunit;
using Xunit;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;

namespace Vuelto.Ui.Tests;

/// <summary>
/// Proves the component-test chassis (v3 TOOL-2): renders the REAL <see cref="Home"/> page against the
/// doubles and asserts both auth branches. If this passes, the chassis correctly fakes localization + drives
/// AuthService signed-in/anonymous state — the foundation the client-UX slices (T39–T42) build on.
/// </summary>
public class HomePageTests : ComponentTestBase
{
    [Fact]
    public void Anonymous_ShowsHeroAndSignInCta()
    {
        var cut = Render<Home>();

        // The deterministic localizer renders keys, so we assert on the key the anonymous branch uses.
        Assert.Contains("Home_SignInCta", cut.Markup);
        Assert.Contains("/login", cut.Markup);
        Assert.DoesNotContain("Home_WelcomeBack", cut.Markup);
    }

    [Fact]
    public async Task SignedIn_TheRootIsTheDashboard()
    {
        // The root IS the dashboard for a signed-in member (no welcome card in between): a fresh household
        // lands on the dashboard's own empty state, with the rate badge and the "new transaction" call.
        await SignInAsync(name: "Ada Lovelace", tenantName: "Test Household");
        Http.On(HttpMethod.Get, "/api/months", "[]");
        Http.On(HttpMethod.Get, "/api/pending-vouchers/count", """{"count":0}""");
        Http.On(HttpMethod.Get, "/api/exchange-rate", """{"rate":510.45,"source":"live","as_of":"2026-09-03T12:00:00+00:00"}""");

        var cut = Render<Home>();

        cut.WaitForElement("[data-testid='dash-empty']");
        Assert.NotNull(cut.Find("[data-testid='dash-page']"));
        Assert.NotNull(cut.Find("[data-testid='dash-new-tx']"));
        cut.WaitForAssertion(() => Assert.Contains("510.45", cut.Find("[data-testid='fx-rate']").TextContent));
        Assert.DoesNotContain("Home_SignInCta", cut.Markup);
        Assert.DoesNotContain("Home_WelcomeBack", cut.Markup);
    }

    [Fact]
    public async Task SignInHelper_DrivesTheRealRefreshEndpoint()
    {
        // Guard the chassis's own contract: SignInAsync must reach signed-in state THROUGH the refresh
        // endpoint (AuthService's real code path), not a reflection shortcut.
        await SignInAsync();

        Assert.True(Auth.IsAuthenticated);
        Assert.Contains(Http.Requests, r =>
            r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/auth/refresh");
    }
}
