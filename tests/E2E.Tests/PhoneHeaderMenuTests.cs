using Microsoft.Playwright;

namespace Vuelto.E2E.Tests;

/// <summary>
/// The user menu on a phone-width window. Below the lg breakpoint the header collapses behind the
/// hamburger and the user-menu panel flows inside that sheet; its full-screen backdrop must stay UNDER
/// the panel, or every tap on Household / Settings / Sign out lands on the backdrop and only closes the
/// menu. That shipped once (the panel was `position: static`, so its z-index did nothing) — found by the
/// Android native smoke, confirmed on a phone, 2026-09-18.
/// </summary>
[TestFixture]
public class PhoneHeaderMenuTests : E2ETestBase
{
    [Test]
    public async Task OnAPhone_TheUserMenuLinksAreTappable()
    {
        await Mailpit.ClearAsync();
        // Sign in at the default (wide) size — the helper waits for the header's user menu, which a
        // phone-width window keeps behind the hamburger — then shrink to a phone.
        await SignInAsync(Page, UniqueEmail("phone-menu"));
        await Page.SetViewportSizeAsync(375, 740);

        await Page.Locator("button.navbar-toggler").ClickAsync();
        await Page.GetByTestId("user-menu").ClickAsync();
        // A real click: Playwright refuses when another element (the backdrop) would receive it.
        await Page.GetByTestId("nav-household").ClickAsync(new() { Timeout = 10_000 });

        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/household$"));
        await Expect(Page.GetByTestId("household-rename-input")).ToBeVisibleAsync(new() { Timeout = 30_000 });
    }
}
