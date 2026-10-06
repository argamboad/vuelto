using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests.Pages;

/// <summary>
/// v4 audit T56 (TB-UI-47..51): the Login page's error table, its 403-by-body rule (R112), the 429 branches
/// and both ways into the MFA prompt; and the Join page's anonymous redirect, which a gate probe must never delay.
/// </summary>
public class LoginAndJoinPageTests : ComponentTestBase
{
    private IRenderedComponent<Login> RenderLogin(string query = "")
    {
        Http.On(HttpMethod.Get, "/api/auth/providers", """{"providers":[]}""");
        Services.GetRequiredService<NavigationManager>().NavigateTo("/login" + query);
        return Render<Login>();
    }

    private async Task<IRenderedComponent<Login>> AtTheCodePromptAsync()
    {
        Http.On(HttpMethod.Post, "/api/auth/otp/send", "{}");
        var page = RenderLogin();
        page.Find("[data-testid=login-email]").Input("someone@example.com");
        await page.Find("[data-testid=login-send-otp]").ClickAsync(new());
        page.Find("[data-testid=login-otp-code]").Input("123456");
        return page;
    }

    // ── TB-UI-48 ──

    [Theory]
    [InlineData("external_failed", "Login_ErrExternalFailed")]
    [InlineData("email_unverified", "Login_ErrEmailUnverified")]
    [InlineData("invalid_link", "Login_ErrInvalidLink")]
    [InlineData("signup_not_allowed", "Login_ErrSignupNotAllowed")]
    [InlineData("unknown_code", "Login_ErrSomethingWrong")] // anything else: the catch-all, never a blank banner
    public void QueryError_MapsToItsOwnCopy(string code, string expectedKey)
    {
        var page = RenderLogin($"?error={code}");

        Assert.Equal(expectedKey, page.Find("[data-testid=login-error]").TextContent.Trim());
    }

    [Fact]
    public void NoQueryError_ShowsNoBanner()
    {
        var page = RenderLogin();

        Assert.Empty(page.FindAll("[data-testid=login-error]"));
    }

    // ── TB-UI-49: R112 — a 403 is a signup refusal only when the API's own body says so ──

    [Fact]
    public async Task Otp403_WithTheSignupBody_ShowsTheSignupCopy()
    {
        var page = await AtTheCodePromptAsync();
        Http.On(HttpMethod.Post, "/api/auth/otp/verify", """{"error":"signup_not_allowed"}""", HttpStatusCode.Forbidden);

        await page.Find("[data-testid=login-verify-otp]").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Contains("Login_ErrSignupNotAllowed", page.Markup));
    }

    [Fact]
    public async Task Otp403_WithAnotherApiError_IsNotExplainedAsASignupRefusal()
    {
        var page = await AtTheCodePromptAsync();
        Http.On(HttpMethod.Post, "/api/auth/otp/verify", """{"error":"something_else"}""", HttpStatusCode.Forbidden);

        await page.Find("[data-testid=login-verify-otp]").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Contains("Login_ErrVerificationFailed", page.Markup));
        Assert.DoesNotContain("Login_ErrSignupNotAllowed", page.Markup);
    }

    [Fact]
    public async Task Otp403_FromAProxy_WithAnHtmlBody_IsNotExplainedAsASignupRefusal()
    {
        var page = await AtTheCodePromptAsync();
        Http.OnHtml(HttpMethod.Post, "/api/auth/otp/verify", "<html><body>403 Forbidden</body></html>", HttpStatusCode.Forbidden);

        await page.Find("[data-testid=login-verify-otp]").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Contains("Login_ErrVerificationFailed", page.Markup));
        Assert.DoesNotContain("Login_ErrSignupNotAllowed", page.Markup);
    }

    // ── TB-UI-50 ──

    [Theory]
    [InlineData("login-send-magic-link", "/api/auth/magic-link/send")]
    [InlineData("login-send-otp", "/api/auth/otp/send")]
    public async Task SendingTooOften_ShowsTooManyRequests(string button, string endpoint)
    {
        Http.On(HttpMethod.Post, endpoint, "{}", HttpStatusCode.TooManyRequests);
        var page = RenderLogin();
        page.Find("[data-testid=login-email]").Input("someone@example.com");

        await page.Find($"[data-testid={button}]").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Contains("Login_ErrTooManyRequests", page.Markup));
        Assert.Empty(page.FindAll("[data-testid=login-otp-code]")); // still on the first step
    }

    [Theory]
    [InlineData("login-send-magic-link", "/api/auth/magic-link/send", "Login_ErrCouldntSendLink")]
    [InlineData("login-send-otp", "/api/auth/otp/send", "Login_ErrCouldntSendCode")]
    public async Task SendingFails_ShowsThatButtonsOwnCopy(string button, string endpoint, string expectedKey)
    {
        Http.On(HttpMethod.Post, endpoint, "{}", HttpStatusCode.InternalServerError);
        var page = RenderLogin();
        page.Find("[data-testid=login-email]").Input("someone@example.com");

        await page.Find($"[data-testid={button}]").ClickAsync(new());

        page.WaitForAssertion(() => Assert.Contains(expectedKey, page.Markup));
    }

    [Fact]
    public async Task AnInvalidEmail_SendsNothing()
    {
        var page = RenderLogin();
        page.Find("[data-testid=login-email]").Input("not-an-email");

        await page.Find("[data-testid=login-send-otp]").ClickAsync(new());

        Assert.DoesNotContain(Http.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/api/auth/otp"));
    }

    // ── TB-UI-51: both ways into the second-factor prompt ──

    [Fact]
    public void MfaChallenge_InTheQuery_OpensTheSecondFactorPrompt()
    {
        var page = RenderLogin("?mfa=challenge-from-a-redirect-login");

        Assert.NotEmpty(page.FindAll("[data-testid=login-mfa-code]"));
        Assert.Empty(page.FindAll("[data-testid=login-email]"));
    }

    [Fact]
    public async Task OtpAccepted_WithMfaRequired_OpensTheSecondFactorPrompt_WithoutASession()
    {
        var page = await AtTheCodePromptAsync();
        Http.On(HttpMethod.Post, "/api/auth/otp/verify", """{"mfa_required":true,"challenge":"abc"}""");

        await page.Find("[data-testid=login-verify-otp]").ClickAsync(new());

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid=login-mfa-code]")));
        Assert.DoesNotContain("/auth-callback", Services.GetRequiredService<NavigationManager>().Uri); // not signed in yet
    }

    [Fact]
    public async Task OtpAccepted_WithoutMfa_GoesToTheCallback()
    {
        var page = await AtTheCodePromptAsync();
        Http.On(HttpMethod.Post, "/api/auth/otp/verify", "{}");

        await page.Find("[data-testid=login-verify-otp]").ClickAsync(new());

        page.WaitForAssertion(() => Assert.EndsWith("/auth-callback", Services.GetRequiredService<NavigationManager>().Uri));
    }

    // ── TB-UI-47: a gate probe never stands between an anonymous invitee and the sign-in prompt ──

    [Fact]
    public void Join_Anonymous_ShowsTheSignInPrompt_EvenWhenTheFeatureProbeNeverAnswers()
    {
        Http.OnGated(HttpMethod.Get, "/api/features"); // hangs forever
        Services.GetRequiredService<NavigationManager>().NavigateTo("/join?token=an-invite");

        var page = Render<Join>();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid=join-needs-signin]")));
        Assert.DoesNotContain(Http.Requests, r => r.RequestUri!.AbsolutePath == "/api/household/invitations/accept");
    }

    [Fact]
    public async Task Join_SignedIn_WithNoToken_OffersManualCodeEntry()
    {
        StubFeatures();
        await SignInAsync();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/join");

        var page = Render<Join>();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid=join-enter-code]")));
    }
}
