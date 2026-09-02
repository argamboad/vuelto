using Microsoft.Playwright;
using Perezosoft.E2E.Tests.Pages;

namespace Perezosoft.E2E.Tests;

/// <summary>
/// v2 audit B8-5: localization end-to-end. Switching the language selector re-renders the UI in the
/// chosen culture (resx-backed <c>IStringLocalizer</c>, EN/ES shipped). Asserts the change by capturing
/// a label before and after the switch rather than hard-coding a translated string, so the test doesn't
/// couple to the exact wording. Maps to the QA plan's i18n case.
/// </summary>
[TestFixture]
public class I18nTests : E2ETestBase
{
    [Test]
    public async Task Switching_Language_ReRendersTheUi()
    {
        var login = new LoginPage(Page);
        await login.GotoAsync();
        await Expect(login.SendOtp).ToBeVisibleAsync(new() { Timeout = 30_000 });

        var english = (await login.SendOtp.InnerTextAsync()).Trim();

        var switcher = Page.GetByTestId("language-switcher");
        await Expect(switcher).ToBeVisibleAsync();
        await switcher.SelectOptionAsync("es"); // triggers a full reload into Spanish

        // After the reload the same control shows a different (Spanish) label.
        await Expect(login.SendOtp).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(login.SendOtp).Not.ToHaveTextAsync(english, new() { Timeout = 15_000 });
    }

    /// <summary>
    /// PREFS-1 (ADR-022) — the QA-I18N-02 journey: a pre-auth login-page language choice is
    /// adopted into the user record on sign-in (the device→server half), and a fresh browser
    /// signing in as the same user comes up in that language with no manual switch (the
    /// server→device reconcile, which persists locally and full-reloads once so the WASM
    /// satellite resources actually load).
    /// </summary>
    [Test]
    public async Task LocaleChoice_FollowsTheUser_AcrossBrowsers()
    {
        await Mailpit.ClearAsync();
        var email = UniqueEmail("i18n");

        // Choose Español while anonymous — device-local only at this point.
        var login = new LoginPage(Page);
        await login.GotoAsync();
        await Expect(login.SendOtp).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Page.GetByTestId("language-switcher").SelectOptionAsync("es"); // full reload
        await Expect(login.SendOtp).ToBeVisibleAsync(new() { Timeout = 30_000 });

        // Signing in adopts the device choice into the user record (PUT /api/auth/locale).
        await Page.RunAndWaitForResponseAsync(
            () => SignInAsync(Page, email),
            r => r.Url.EndsWith("/api/auth/locale") && r.Request.Method == "PUT",
            new() { Timeout = 60_000 });

        // "New device": a fresh context boots in English, and the sign-in reconcile pulls
        // the saved locale (persist + one reload). Wait for the device cache to flip, then
        // verify the UI actually runs in Spanish via the settings switcher's value.
        await Mailpit.ClearAsync();
        await using var secondCtx = await Browser.NewContextAsync(ContextOptions());
        var secondPage = await secondCtx.NewPageAsync();
        await SignInAsync(secondPage, email);

        await secondPage.WaitForFunctionAsync(
            "() => localStorage.getItem('app_culture') === 'es'",
            null, new() { Timeout = 30_000 });
        await secondPage.GotoAsync("/settings");
        await Expect(secondPage.GetByTestId("language-switcher")).ToHaveValueAsync("es", new() { Timeout = 30_000 });
    }
}
