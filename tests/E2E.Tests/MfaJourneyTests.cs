using Microsoft.Playwright;
using Perezosoft.E2E.Tests.Pages;

namespace Perezosoft.E2E.Tests;

/// <summary>
/// v2 audit B8-5: the security-critical MFA journey end-to-end through the real Blazor app + API.
/// A user signs in, enrolls authenticator TOTP from the settings card (reading the shown secret and
/// computing a live code), signs out, and signs in again — where the second factor is now enforced:
/// the OTP step yields a step-up prompt, and only a valid TOTP completes sign-in. This exercises the
/// whole MFA-1/MFA-2 surface (enroll → confirm → login step-up) at the browser, complementing the
/// service-level integration tests. Maps to the QA plan's MFA cases.
/// </summary>
[TestFixture]
public class MfaJourneyTests : E2ETestBase
{
    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    [Test]
    public async Task Enroll_ThenStepUp_OnNextSignIn()
    {
        await Mailpit.ClearAsync();
        var email = $"e2e-mfa-{Guid.NewGuid():N}@example.com";

        // Sign in (creates the account) and land in the app shell.
        var login = new LoginPage(Page);
        await login.GotoAsync();
        await Expect(login.Email).ToBeVisibleAsync(Slow);
        await login.SignInWithOtpAsync(email);
        await Expect(Page.GetByTestId("sign-out")).ToBeVisibleAsync(Slow);

        // Enroll authenticator TOTP from the settings card.
        var settings = new SettingsPage(Page);
        await settings.GotoAsync();
        var secret = await settings.EnrollMfaAsync();

        // Sign out, then sign back in — MFA is now enforced, so a step-up code is required.
        await Page.GetByTestId("sign-out").ClickAsync();
        await Expect(login.Email).ToBeVisibleAsync(Slow);

        await Mailpit.ClearAsync();
        await login.SignInWithOtpAndMfaAsync(email, secret);

        // Second factor satisfied → back in the app shell.
        await Expect(Page.GetByTestId("sign-out")).ToBeVisibleAsync(Slow);
        await Expect(Page.GetByTestId("tenant-badge")).ToBeVisibleAsync();
    }

    [Test]
    public async Task StepUp_WithWrongCode_DoesNotSignIn()
    {
        await Mailpit.ClearAsync();
        var email = $"e2e-mfa-{Guid.NewGuid():N}@example.com";

        var login = new LoginPage(Page);
        await login.GotoAsync();
        await Expect(login.Email).ToBeVisibleAsync(Slow);
        await login.SignInWithOtpAsync(email);
        await Expect(Page.GetByTestId("sign-out")).ToBeVisibleAsync(Slow);

        var settings = new SettingsPage(Page);
        await settings.GotoAsync();
        await settings.EnrollMfaAsync();

        await Page.GetByTestId("sign-out").ClickAsync();
        await Expect(login.Email).ToBeVisibleAsync(Slow);

        // Sign in to the step-up prompt, then submit a bad code — sign-in must not complete.
        await Mailpit.ClearAsync();
        await login.SignInWithOtpAsync(email);
        await Expect(login.MfaCode).ToBeVisibleAsync(Slow);
        await login.MfaCode.FillAsync("000000");
        await login.VerifyMfa.ClickAsync();

        await Expect(login.MfaCode).ToBeVisibleAsync();                 // still on the step-up prompt…
        await Expect(Page.GetByTestId("sign-out")).Not.ToBeVisibleAsync(); // …not in the app
    }
}
