using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Perezosoft.E2E.Tests.Pages;

namespace Perezosoft.E2E.Tests;

/// <summary>
/// End-to-end auth flow through the real Blazor WASM app + API + Mailpit. Covers the
/// QA plan's web smoke path that doesn't need an external OAuth provider: email OTP
/// sign-in, sign-out, and login validation. (Maps to QA-SMK-01/03 and QA-AUTH-09.)
/// </summary>
[TestFixture]
public class AuthFlowTests : E2ETestBase
{
    // Blazor WASM boots the .NET runtime on first hit — be generous.
    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    [Test]
    public async Task Login_Page_Renders()
    {
        var login = new LoginPage(Page);
        await login.GotoAsync();

        await Expect(login.Email).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(login.SendOtp).ToBeVisibleAsync();
    }

    [Test]
    public async Task Otp_SignIn_LandsInTheApp()
    {
        await Mailpit.ClearAsync();
        var email = UniqueEmail();

        var login = new LoginPage(Page);
        await login.GotoAsync();
        await Expect(login.Email).ToBeVisibleAsync(new() { Timeout = 30_000 });

        await login.SignInWithOtpAsync(email);

        // Signed in → the app shell (sign-out + tenant badge) is shown.
        await Expect(Page.GetByTestId("sign-out")).ToBeVisibleAsync(Slow);
        await Expect(Page.GetByTestId("tenant-badge")).ToBeVisibleAsync();
    }

    [Test]
    public async Task SignOut_ReturnsToLogin()
    {
        await Mailpit.ClearAsync();
        var email = UniqueEmail();

        var login = new LoginPage(Page);
        await login.GotoAsync();
        await Expect(login.Email).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await login.SignInWithOtpAsync(email);
        await Expect(Page.GetByTestId("sign-out")).ToBeVisibleAsync(Slow);

        await Page.GetByTestId("sign-out").ClickAsync();

        await Expect(login.Email).ToBeVisibleAsync(Slow);   // back on /login
        await Expect(Page).ToHaveURLAsync(new Regex(".*/login"));
    }

    [Test]
    public async Task Otp_Lockout_ShowsDistinctTooManyAttemptsMessage()
    {
        await Mailpit.ClearAsync();
        var email = UniqueEmail();

        var login = new LoginPage(Page);
        await login.GotoAsync();
        await Expect(login.Email).ToBeVisibleAsync(new() { Timeout = 30_000 });

        // Request a real code so we can submit a guaranteed-wrong variant of it.
        var code = await login.RequestOtpAndReadCodeAsync(email);
        var wrong = WrongVariant(code);

        // Burn the whole per-email budget (Auth:Otp:MaxAttempts, default 5) on wrong guesses. The
        // E2E stack raises the per-IP limit to 1000, so the 429 throttle can't mask the lockout.
        for (var i = 0; i < 5; i++)
            await login.SubmitOtpAsync(wrong);

        // The lockout must read as its own signal — NOT the generic "incorrect or has expired", which
        // would (wrongly) suggest the user just mistyped and should retry. (QA-AUTH-04, CONF-5.)
        await Expect(login.OtpError).ToContainTextAsync("Too many attempts", new() { Timeout = 15_000 });
    }

    // Flip the first digit so the guess is certainly wrong but still a well-formed 6-digit code.
    private static string WrongVariant(string code)
    {
        var chars = code.ToCharArray();
        chars[0] = chars[0] == '0' ? '1' : '0';
        return new string(chars);
    }

    [Test]
    public async Task Invalid_Email_IsRejected_NoCodeStep()
    {
        var login = new LoginPage(Page);
        await login.GotoAsync();
        await Expect(login.Email).ToBeVisibleAsync(new() { Timeout = 30_000 });

        await login.Email.FillAsync("not-an-email");
        await login.SendOtp.ClickAsync();

        // Validation blocks the request, so the code-entry step never appears.
        await Expect(login.OtpCode).Not.ToBeVisibleAsync();
        await Expect(login.Email).ToHaveClassAsync(new Regex("is-invalid"));
    }

    private static string UniqueEmail() => $"e2e-{Guid.NewGuid():N}@example.com";
}
