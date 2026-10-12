using System.Net;
using Bunit;
using Xunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Shared.Ui.Auth;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;

namespace Vuelto.Ui.Tests;

/// <summary>
/// GATES-2 (ADR-027), client half. A refused signup is a policy, not a fault, and every sign-in surface
/// has to say so — otherwise an invited-only deployment tells the people it turned away that the app is
/// broken, and they report a bug.
/// <para>
/// The refusal reaches the client three ways and all three are covered here: a query parameter (OAuth
/// and magic link both redirect to the login page), a 403 on the web OTP post, and a server error code
/// in the body on the native OTP path.
/// </para>
/// </summary>
public class SignupRefusedCopyTests : ComponentTestBase
{
    [Fact]
    public void LoginPage_ReadsTheRefusalFromTheQueryString()
    {
        // How OAuth and magic link arrive: the server bounced the browser back with ?error=…
        Services.GetRequiredService<NavigationManager>().NavigateTo("/login?error=signup_not_allowed");

        var page = Render<Login>();

        Assert.Contains("Login_ErrSignupNotAllowed", page.Markup);
        Assert.DoesNotContain("Login_ErrSomethingWrong", page.Markup); // not the catch-all
    }

    [Fact]
    public async Task WebOtp_RefusedSignup_ReadsAsPolicy_NotAsAWrongCode()
    {
        // The distinction that matters: a 401 means "check the code you typed", a 403 means "no code will
        // ever work here". Collapsing them would send someone hunting for a typo forever.
        Http.On(HttpMethod.Post, "/api/auth/otp/send", "{}");
        Http.On(HttpMethod.Post, "/api/auth/otp/verify",
            """{"error":"signup_not_allowed"}""", HttpStatusCode.Forbidden);

        var page = Render<Login>();
        page.Find("[data-testid=login-email]").Input("stranger@example.com");
        await page.Find("[data-testid=login-send-otp]").ClickAsync(new());
        page.Find("[data-testid=login-otp-code]").Input("123456");
        await page.Find("[data-testid=login-verify-otp]").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Contains("Login_ErrSignupNotAllowed", page.Markup));
        Assert.DoesNotContain("Login_ErrCodeIncorrect", page.Markup);
    }

    [Fact]
    public void NativeOtp_MapsTheRefusal_ThroughTheSharedCopyHelper()
    {
        // Native reads the server's error code out of the body rather than the status line, so the shared
        // mapping is the only thing keeping the two surfaces from drifting apart.
        Assert.Equal("Login_ErrSignupNotAllowed", AuthErrorCopy.OtpErrorKey("signup_not_allowed"));
        Assert.Equal("Login_ErrTooManyAttempts", AuthErrorCopy.OtpErrorKey("too_many_attempts"));
        Assert.Equal("Login_ErrCodeIncorrect", AuthErrorCopy.OtpErrorKey("invalid_code"));
    }
}
