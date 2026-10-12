using Microsoft.Playwright;

namespace Vuelto.E2E.Tests.Pages;

/// <summary>Page object for the account settings screen — drives the MFA (TOTP) enrollment card.</summary>
public class SettingsPage(IPage page) : BasePage(page)
{
    public override string Path => "/settings";

    public ILocator NotifPrefInApp => Page.GetByTestId("notif-prefs-inapp");
    public ILocator NotifPrefEmail => Page.GetByTestId("notif-prefs-email");

    public ILocator DeleteAccount => Page.GetByTestId("delete-account");

    public ILocator MfaEnable => Page.GetByTestId("mfa-enable");
    public ILocator MfaSecret => Page.GetByTestId("mfa-secret");
    public ILocator MfaConfirmCode => Page.GetByTestId("mfa-confirm-code");
    public ILocator MfaVerify => Page.GetByTestId("mfa-verify");
    public ILocator MfaSavedCodes => Page.GetByTestId("mfa-saved-codes");
    public ILocator MfaStatusOn => Page.GetByTestId("mfa-status-on");

    /// <summary>
    /// Enrolls the signed-in user in authenticator TOTP through the UI: enable → read the shown secret →
    /// compute + submit a TOTP → acknowledge the recovery codes. Returns the Base32 secret so the caller
    /// can later compute a step-up code.
    /// </summary>
    public async Task<string> EnrollMfaAsync()
    {
        await MfaEnable.ClickAsync();
        await Assertions.Expect(MfaSecret).ToBeVisibleAsync(new() { Timeout = 15_000 });

        var secret = (await MfaSecret.InnerTextAsync()).Trim();
        await MfaConfirmCode.FillAsync(Totp.Now(secret));
        await MfaVerify.ClickAsync();

        // Enrollment succeeded → the one-time recovery codes are shown; acknowledge to finish.
        await Assertions.Expect(MfaSavedCodes).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await MfaSavedCodes.ClickAsync();
        await Assertions.Expect(MfaStatusOn).ToBeVisibleAsync(new() { Timeout = 15_000 });

        return secret;
    }
}
