using Microsoft.Playwright;

namespace Vuelto.E2E.Tests;

/// <summary>
/// Dark-mode journey (THEME-1 + PREFS-1, ADR-022): the header switcher flips
/// <c>data-bs-theme</c> live (no reload), the choice survives a reload via the pre-paint
/// localStorage bootstrap, and — because it's saved per user server-side — a fresh browser
/// ("new device") gets it applied AS PART OF SIGNING IN, with no manual reload (the
/// <c>AuthService.SignedIn</c> reconcile). "system" is a stored preference too: switching
/// back to it on one device returns the others to the OS scheme on their next reconcile.
/// Maps to QA-SET-08.
/// </summary>
[TestFixture]
public class ThemeJourneyTests : E2ETestBase
{
    [Test]
    public async Task ThemeChoice_AppliesLive_PersistsLocally_AndFollowsTheUser()
    {
        await Mailpit.ClearAsync();
        var email = UniqueEmail("theme");

        await SignInAsync(Page, email);

        // Default is the OS scheme; the Playwright browser emulates light.
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", "light");

        // Flip to dark from the header: applies live, no reload. The switcher saves the
        // choice server-side with a best-effort background PUT; wait for it so the steps
        // below can't race it (the "follows the user" legs depend on it).
        await Page.RunAndWaitForResponseAsync(
            () => Page.GetByTestId("theme-switcher").SelectOptionAsync("dark"),
            r => r.Url.EndsWith("/api/auth/theme") && r.Request.Method == "PUT");
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", "dark");

        // Survives a reload: theme.js re-applies from localStorage before first paint.
        await Page.ReloadAsync();
        await Expect(Page.GetByTestId("sign-out")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", "dark");

        // "New device": a fresh context has no localStorage, so it renders light — and the
        // sign-in itself reconciles it to the server-stored dark (PREFS-1: no manual reload).
        // Drop the first sign-in's email first so the OTP poll can't misread the consumed code.
        await Mailpit.ClearAsync();
        await using var secondCtx = await Browser.NewContextAsync(ContextOptions());
        var secondPage = await secondCtx.NewPageAsync();
        await SignInAsync(secondPage, email);
        await Expect(secondPage.Locator("html")).ToHaveAttributeAsync("data-bs-theme", "dark", new() { Timeout = 30_000 });

        // "system" is a real preference (stored, not null): switching back on the second
        // device propagates — the first device returns to the OS scheme (light) when its
        // next cold start reconciles.
        await secondPage.RunAndWaitForResponseAsync(
            () => secondPage.GetByTestId("theme-switcher").SelectOptionAsync("system"),
            r => r.Url.EndsWith("/api/auth/theme") && r.Request.Method == "PUT");
        await Expect(secondPage.Locator("html")).ToHaveAttributeAsync("data-bs-theme", "light");

        await Page.ReloadAsync();
        await Expect(Page.GetByTestId("sign-out")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", "light", new() { Timeout = 30_000 });
    }
}
