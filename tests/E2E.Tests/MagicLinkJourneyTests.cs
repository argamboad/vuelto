using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Vuelto.E2E.Tests.Pages;

namespace Vuelto.E2E.Tests;

/// <summary>
/// Magic-link sign-in journey (E2E-4): request the link, read it from Mailpit, open it, land
/// signed-in — the redirect path MFA-3 hardened (verify → refresh cookie → /auth-callback).
/// Also the single-use unhappy path: a consumed link bounces to /login?error=invalid_link.
/// Maps to QA-AUTH-01 (happy path) and QA-AUTH-05 (single-use).
/// </summary>
[TestFixture]
public class MagicLinkJourneyTests : E2ETestBase
{
    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    [Test]
    public async Task MagicLink_SignIn_LandsInTheApp()
    {
        await Mailpit.ClearAsync();
        var email = UniqueEmail("magic");

        var login = new LoginPage(Page);
        await login.GotoAsync();
        await Expect(login.Email).ToBeVisibleAsync(Slow);

        var link = await login.RequestMagicLinkAsync(email);

        // Opening the emailed link verifies the token on the API, sets the refresh cookie,
        // and bounces through /auth-callback into the app shell.
        await Page.GotoAsync(link);
        await Expect(Page.GetByTestId("sign-out")).ToBeVisibleAsync(Slow);
        await Expect(Page.GetByTestId("tenant-badge")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Used_MagicLink_IsRejected()
    {
        await Mailpit.ClearAsync();
        var email = UniqueEmail("magic-reuse");

        var login = new LoginPage(Page);
        await login.GotoAsync();
        await Expect(login.Email).ToBeVisibleAsync(Slow);

        var link = await login.RequestMagicLinkAsync(email);
        await Page.GotoAsync(link);
        await Expect(Page.GetByTestId("sign-out")).ToBeVisibleAsync(Slow);

        // The token is single-use: opening the same link again (fresh context, no session)
        // must not sign in — it bounces to the login page with the invalid-link error.
        await using var secondCtx = await Browser.NewContextAsync(ContextOptions());
        var secondPage = await secondCtx.NewPageAsync();
        await secondPage.GotoAsync(link);

        await Expect(secondPage).ToHaveURLAsync(new Regex(".*/login\\?error=invalid_link"), new() { Timeout = 30_000 });
        await Expect(new LoginPage(secondPage).ErrorAlert).ToBeVisibleAsync(Slow);
        await Expect(secondPage.GetByTestId("sign-out")).Not.ToBeVisibleAsync();
    }
}
