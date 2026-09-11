using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Shared.Ui.Components;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// SKIN-4: the header's user menu. The frames show only a tenant chip and a name on the right, so the six
/// destinations that used to sit loose in the bar moved behind that chip.
///
/// The contract the E2E suite leans on is asserted here rather than discovered in CI: sign-out is ALWAYS in
/// the DOM (NativeSmokeTests waits on it in the attached state behind the phone hamburger) but is hidden
/// until the menu opens, and the always-visible trigger is what now signals "the app shell rendered".
/// </summary>
public class HeaderMenuTests : ComponentTestBase
{
    private async Task<IRenderedComponent<AppHeader>> HeaderAsync()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/pending-vouchers/count", """{"count":0}""");
        var cut = Render<AppHeader>();
        cut.WaitForElement("[data-testid='user-menu']");
        return cut;
    }

    [Fact]
    public async Task TheTriggerIsAlwaysThere_AndCarriesTheTenantAndTheName()
    {
        var cut = await HeaderAsync();

        Assert.Equal("false", cut.Find("[data-testid='user-menu']").GetAttribute("aria-expanded"));
        Assert.Equal("Test Household", cut.Find("[data-testid='tenant-badge']").TextContent.Trim());
        Assert.Contains("Ada Lovelace", cut.Find("[data-testid='user-menu']").TextContent);
    }

    [Fact]
    public async Task SignOutIsAttachedButHidden_UntilTheMenuOpens()
    {
        var cut = await HeaderAsync();

        // Attached — a conditional render would break the native smoke test, which waits on exactly this.
        Assert.True(cut.Find("[data-testid='user-menu-panel']").HasAttribute("hidden"));
        Assert.NotNull(cut.Find("[data-testid='sign-out']"));

        cut.Find("[data-testid='user-menu']").Click();

        Assert.False(cut.Find("[data-testid='user-menu-panel']").HasAttribute("hidden"));
        Assert.Equal("true", cut.Find("[data-testid='user-menu']").GetAttribute("aria-expanded"));
    }

    [Fact]
    public async Task EveryDestinationThatLeftTheBarIsStillReachable()
    {
        var cut = await HeaderAsync();
        cut.Find("[data-testid='user-menu']").Click();

        var panel = cut.Find("[data-testid='user-menu-panel']");
        Assert.Equal("/household", panel.QuerySelector("a[href='/household']")!.GetAttribute("href"));
        Assert.Equal("/billing", panel.QuerySelector("[data-testid='nav-billing']")!.GetAttribute("href"));
        Assert.Equal("/settings", panel.QuerySelector("a[href='/settings']")!.GetAttribute("href"));
        Assert.NotNull(panel.QuerySelector("[data-testid='sign-out']"));
    }

    [Fact]
    public async Task TheBellStaysOutsideTheMenu_BecauseAnUnreadCountYouCannotSeeIsNotAnIndicator()
    {
        var cut = await HeaderAsync();

        var bell = cut.Find("[data-testid='notif-bell']");
        Assert.Null(bell.Closest("[data-testid='user-menu-panel']"));
    }

    [Fact]
    public async Task EscapeClosesIt()
    {
        var cut = await HeaderAsync();
        cut.Find("[data-testid='user-menu']").Click();
        Assert.False(cut.Find("[data-testid='user-menu-panel']").HasAttribute("hidden"));

        cut.Find(".user-menu").KeyDown(Key.Escape);

        Assert.True(cut.Find("[data-testid='user-menu-panel']").HasAttribute("hidden"));
    }

    [Fact]
    public async Task NavigatingAwayClosesIt_SoItNeverHangsOverTheNewPage()
    {
        var cut = await HeaderAsync();
        cut.Find("[data-testid='user-menu']").Click();

        Services.GetRequiredService<NavigationManager>().NavigateTo("http://localhost/settings");

        cut.WaitForAssertion(() => Assert.True(cut.Find("[data-testid='user-menu-panel']").HasAttribute("hidden")));
    }

    [Fact]
    public async Task TheAdminLinkIsStaffOnly()
    {
        Http.On(HttpMethod.Get, "/api/pending-vouchers/count", """{"count":0}""");
        await SignInAsync();
        var member = Render<AppHeader>();
        member.WaitForElement("[data-testid='user-menu']");
        member.Find("[data-testid='user-menu']").Click();
        Assert.Empty(member.FindAll("[data-testid='nav-admin']"));
    }
}
