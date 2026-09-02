using Microsoft.Playwright;

namespace Perezosoft.E2E.Tests.Pages;

/// <summary>Page object for the login screen (selectors via stable data-testid hooks).</summary>
public class LoginPage(IPage page) : BasePage(page)
{
    public override string Path => "/login";

    public ILocator Email => Page.GetByTestId("login-email");
    public ILocator SendOtp => Page.GetByTestId("login-send-otp");
    public ILocator SendMagicLink => Page.GetByTestId("login-send-magic-link");
    public ILocator ErrorAlert => Page.GetByTestId("login-error");
    public ILocator OtpError => Page.GetByTestId("login-otp-error");
    public ILocator OtpCode => Page.GetByTestId("login-otp-code");
    public ILocator VerifyOtp => Page.GetByTestId("login-verify-otp");
    public ILocator MfaCode => Page.GetByTestId("login-mfa-code");
    public ILocator VerifyMfa => Page.GetByTestId("login-verify-mfa");

    /// <summary>Requests a magic link and returns the sign-in URL read from Mailpit.</summary>
    public async Task<string> RequestMagicLinkAsync(string email)
    {
        await Email.FillAsync(email);
        await SendMagicLink.ClickAsync();
        return await Mailpit.WaitForMagicLinkAsync(email, TimeSpan.FromSeconds(20));
    }

    /// <summary>Requests an OTP, reads it from Mailpit, enters it, and submits.</summary>
    public async Task SignInWithOtpAsync(string email)
    {
        await Email.FillAsync(email);
        await SendOtp.ClickAsync();

        var code = await Mailpit.WaitForOtpAsync(email, TimeSpan.FromSeconds(20));
        await OtpCode.FillAsync(code);
        await VerifyOtp.ClickAsync();
    }

    /// <summary>Requests an OTP and returns the real code read from Mailpit (without submitting it).</summary>
    public async Task<string> RequestOtpAndReadCodeAsync(string email)
    {
        await Email.FillAsync(email);
        await SendOtp.ClickAsync();
        return await Mailpit.WaitForOtpAsync(email, TimeSpan.FromSeconds(20));
    }

    /// <summary>Enters a code into the OTP field and submits it.</summary>
    public async Task SubmitOtpAsync(string code)
    {
        await OtpCode.FillAsync(code);
        await VerifyOtp.ClickAsync();
    }

    /// <summary>
    /// OTP sign-in for a user with MFA enabled: after the OTP step the app presents the step-up
    /// challenge; compute a fresh TOTP from <paramref name="totpSecret"/>, enter it, and submit.
    /// </summary>
    public async Task SignInWithOtpAndMfaAsync(string email, string totpSecret)
    {
        await SignInWithOtpAsync(email);
        await Assertions.Expect(MfaCode).ToBeVisibleAsync(new() { Timeout = 30_000 }); // step-up prompt
        await MfaCode.FillAsync(Totp.Now(totpSecret));
        await VerifyMfa.ClickAsync();
    }
}
