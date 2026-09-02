using Microsoft.Playwright;
using NUnit.Framework;

namespace Perezosoft.E2E.Tests;

/// <summary>
/// Native smoke (NATIVE-7, ADR-018): drives the REAL MAUI app over the Chrome DevTools Protocol —
/// the app's WebView is launched with remote debugging enabled (WebView2:
/// <c>WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9223</c>; Android WebView:
/// an <c>adb forward</c> to its devtools socket) and Playwright connects with
/// <see cref="IBrowserType.ConnectOverCDPAsync"/>. Deliberately tiny (auth + one authorized page):
/// native UI automation is slower and flakier than browser E2E, so this is a boot-and-sign-in
/// canary, not a regression suite — the per-feature matrix stays manual (QA plan §12–13b).
///
/// [Explicit] keeps it out of the default `dotnet test` run (the browser e2e job); the CI native
/// smoke selects it with <c>--filter "Category=NativeSmoke"</c>. Config via env:
/// NATIVE_SMOKE_CDP (default http://127.0.0.1:9223 — IPv4 literal, not localhost: WebView2 150+
/// binds CDP on IPv4 loopback only), MAILPIT_BASE_URL (the Mailpit helper).
///
/// NOT CURRENTLY CI-WIRED (2026-07-17). WebView2 Runtime 150 strips <c>--remote-debugging-port</c>
/// under an elevated host (GitHub Windows runners run elevated), so the CDP endpoint never binds and
/// this can't attach there (MicrosoftEdge/WebView2Feedback#5640). The <c>native-smoke-windows</c> CI
/// leg was de-scoped to a process-alive + provider-probe boot check (matching the Apple leg); the
/// full UI-driving journey still runs on the Android leg (<c>tests/native-smoke-android/smoke.js</c>).
/// This fixture stays for LOCAL use on a NON-elevated box, and is the target to re-wire if the Windows
/// leg later pins Fixed-Version WebView2 149 or launches the app de-elevated.
/// </summary>
[TestFixture]
[Explicit("Needs a running native app with CDP enabled — launched by the native-smoke CI job")]
[Category("NativeSmoke")]
public class NativeSmokeTests
{
    private static string CdpUrl =>
        Environment.GetEnvironmentVariable("NATIVE_SMOKE_CDP") ?? "http://127.0.0.1:9223";

    [Test]
    public async Task Native_App_Boots_SignsIn_And_LoadsHousehold()
    {
        using var pw = await Playwright.CreateAsync();

        // The app may still be starting when the runner reaches this test — retry the CDP connect.
        IBrowser? browser = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (true)
        {
            try
            {
                browser = await pw.Chromium.ConnectOverCDPAsync(CdpUrl);
                break;
            }
            catch when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(3000);
            }
        }

        var page = browser.Contexts[0].Pages[0];
        TestContext.Out.WriteLine($"connected: {page.Url}");

        // Boot: the login screen renders (a startup crash — like G7 on iOS — dies here).
        var emailBox = page.GetByTestId("login-email");
        await emailBox.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60_000 });

        // OTP sign-in end-to-end through the real API + Mailpit (the native body-token transport).
        var email = $"native-smoke-{Guid.NewGuid():N}@example.com";
        await Mailpit.ClearAsync();
        await emailBox.FillAsync(email);
        await page.GetByTestId("login-send-otp").ClickAsync();
        var code = await Mailpit.WaitForOtpAsync(email, TimeSpan.FromSeconds(60));
        await page.GetByTestId("login-otp-code").FillAsync(code);
        await page.GetByTestId("login-verify-otp").ClickAsync();
        // Attached, not Visible: the CI runner opens the app window narrow enough that the
        // responsive header collapses sign-out behind the hamburger. Its presence in the DOM
        // proves the authenticated shell rendered; the household assertions below prove the rest.
        await page.GetByTestId("sign-out").First.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 60_000 });

        // One authorized page: Household loads its data — proves the native Bearer path.
        await page.GotoAsync("https://0.0.0.1/household");
        await page.GetByTestId("household-rename-input").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        var members = page.GetByTestId("member-row");
        await Assertions.Expect(members).ToHaveCountAsync(1, new() { Timeout = 30_000 });

        TestContext.Out.WriteLine("native smoke: boot + OTP sign-in + household roster OK");
    }
}
