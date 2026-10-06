using Microsoft.Playwright;

namespace Vuelto.E2E.Tests;

/// <summary>
/// v4 audit T58 (TB-UI-69..71): three session behaviours that only a real browser can prove and that were
/// held by unit tests or string checks alone.
/// <list type="bullet">
/// <item><b>Back after sign-out</b> (QA-SEC-03): <c>bfcache-guard.js</c> reloads a page the browser restores
/// from its back/forward cache, so the previous user's household is not one keypress away on a shared
/// computer. The old check only looked for three strings in the file — an inverted condition would pass.</item>
/// <item><b>Keep-alive past the access token's expiry</b>: the client renews before the hour is up, once.</item>
/// <item><b>A theme saved while a renewal is in flight</b> survives a reload (#233, whose first fix signed
/// users out after a reload).</item>
/// </list>
/// </summary>
[TestFixture]
public class SessionJourneyTests : E2ETestBase
{
    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    [Test]
    public async Task SignOut_ThenBack_LandsOnLogin_NotTheCachedHousehold()
    {
        // Playwright starts Chromium with the back/forward cache OFF, so a plain Back would do an ordinary load
        // and this journey would pass without ever meeting the guard. Launch a browser with that default flag
        // removed, and PROVE below that the page really was restored from the cache.
        // (Channel "chromium" is the full browser in its new headless mode; the default headless shell has no
        // back/forward cache at all.)
        await using var browser = await Playwright.Chromium.LaunchAsync(new()
        {
            Channel = "chromium",
            IgnoreDefaultArgs = ["--disable-back-forward-cache"],
        });
        await using var context = await browser.NewContextAsync(ContextOptions());
        // Recorded in sessionStorage because the guard's reload throws the page's own memory away.
        await context.AddInitScriptAsync(
            "addEventListener('pageshow', e => { if (e.persisted) sessionStorage.setItem('e2e_bfcache_restored', '1'); });");
        var page = await context.NewPageAsync();
        BlazorBoot.Watch(page);

        var household = await SignInToHouseholdAsync(page, UniqueEmail("back"));
        await Assertions.Expect(household.RenameInput).ToBeVisibleAsync(Slow);

        await page.GetByTestId("user-menu").ClickAsync();   // SKIN-4: sign out lives in the user menu
        await page.GetByTestId("sign-out").ClickAsync();
        await Assertions.Expect(page.GetByTestId("login-email")).ToBeVisibleAsync(Slow);

        await page.GoBackAsync();

        // Back lands on the login page; the household is never shown again.
        await Assertions.Expect(page.GetByTestId("login-email")).ToBeVisibleAsync(Slow);
        await Assertions.Expect(page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/login"));
        await Assertions.Expect(page.GetByTestId("household-rename-input")).ToHaveCountAsync(0);

        var restored = await page.EvaluateAsync<string?>("sessionStorage.getItem('e2e_bfcache_restored')");
        var why = await page.EvaluateAsync<string>("JSON.stringify(performance.getEntriesByType('navigation')[0]?.notRestoredReasons ?? null)");
        Assert.That(restored, Is.EqualTo("1"),
            $"(notRestoredReasons: {why}) The browser did not restore the page from its back/forward cache, so the guard was never exercised — "
            + "this journey would pass with the guard deleted. Check the launch flag against the pinned Playwright version.");
    }

    [Test]
    public async Task KeepAlive_PastTheAccessTokenExpiry_SessionSurvives_WithOneRenewal()
    {
        // The page's clock is Playwright's: timers and Date both move when the test says so, and .NET's timers in
        // the browser are built on them. An hour passes in a moment; the server's clock is untouched, which is
        // exactly the keep-alive's job — renew BEFORE the token it holds runs out.
        await Page.Clock.InstallAsync();
        await SignInToHouseholdAsync(Page, UniqueEmail("keepalive"));
        await Assertions.Expect(Page.GetByTestId("member-row")).ToHaveCountAsync(1, new() { Timeout = 30_000 });

        var renewals = 0;
        Page.Request += (_, request) =>
        {
            if (request.Method == "POST" && request.Url.EndsWith("/api/auth/refresh")) renewals++;
        };

        // The access token lasts 60 minutes and the client renews one minute before it expires.
        var renewed = Page.WaitForResponseAsync(r => r.Url.EndsWith("/api/auth/refresh") && r.Status == 200, new() { Timeout = 30_000 });
        await Page.Clock.FastForwardAsync("59:30");
        await renewed;
        await Page.Clock.FastForwardAsync("02:00"); // past the old token's expiry
        await Page.Clock.ResumeAsync();             // let time flow again: rendering is timer-driven too

        Assert.That(renewals, Is.EqualTo(1), "the session renews once per token lifetime, not in a loop");

        // Still signed in, and an authorized call still works: reach a page that loads its data from the API.
        await Page.Locator("a.navbar-brand").ClickAsync(); // leave, then come back: a fresh authorized load
        await Page.GetByTestId("user-menu").ClickAsync();   // SKIN-4: Household lives in the user menu
        await Page.GetByTestId("nav-household").ClickAsync();
        await Assertions.Expect(Page.GetByTestId("member-row")).ToHaveCountAsync(1, new() { Timeout = 30_000 });
        await Assertions.Expect(Page.GetByTestId("user-menu")).ToBeVisibleAsync(); // the signed-in sentinel (SKIN-4)
    }

    [Test]
    public async Task ThemeSave_WithARenewalInFlight_SurvivesReload()
    {
        await Mailpit.ClearAsync();
        await SignInAsync(Page, UniqueEmail("theme-race"));
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", "light");

        // Every renewal from here on is slow: the theme save's follow-up refresh is still in flight when the
        // page is reloaded. The first fix for #233 lost the session at exactly this point.
        await Page.RouteAsync("**/api/auth/refresh", async route =>
        {
            await Task.Delay(1_500);
            await route.ContinueAsync();
        });

        await Page.GetByTestId("user-menu").ClickAsync();   // SKIN-4: the theme switcher lives in the user menu
        await Page.RunAndWaitForResponseAsync(
            () => Page.GetByTestId("theme-switcher").SelectOptionAsync("dark"),
            r => r.Url.EndsWith("/api/auth/theme") && r.Request.Method == "PUT");
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", "dark");

        await BlazorBoot.ReloadAsync(Page);

        await Expect(Page.GetByTestId("user-menu")).ToBeVisibleAsync(Slow); // still signed in after the reload (SKIN-4 sentinel)
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-bs-theme", "dark");
        await Page.GetByTestId("user-menu").ClickAsync();
        await Expect(Page.GetByTestId("theme-switcher")).ToHaveValueAsync("dark");
    }
}
