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
    public async Task SignedIn_ShowsWelcomeWithTheDisplayName()
    {
        await SignInAsync(name: "Ada Lovelace", tenantName: "Test Household");

        var cut = Render<Home>();

        // Home_WelcomeBack is a parameterized key; the fake localizer echoes the argument, so the display
        // name flowing from the JWT through AuthService into the component is observable.
        Assert.Contains("Home_WelcomeBack[Ada Lovelace]", cut.Markup);
        Assert.Contains("Home_SignedInTo[Test Household]", cut.Markup);
        Assert.DoesNotContain("Home_SignInCta", cut.Markup);
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
