using Microsoft.Playwright;
using Perezosoft.E2E.Tests.Pages;

namespace Perezosoft.E2E.Tests;

/// <summary>
/// v3 audit TB-UI-16 (T45c) — the one place UX-1 is caught end-to-end: a signed-in user whose saved
/// locale differs from the boot culture opens an invite link. MainLayout's reconcile does its ONE
/// satellite-assembly reload — and that reload must PRESERVE <c>/join?token=…</c> (the old bug bounced
/// every anonymous path to "/", destroying the invite-acceptance journey mid-flight).
/// </summary>
[TestFixture]
public class LocaleMismatchJoinTests : E2ETestBase
{
    [Test]
    public async Task Reconcile_DoesNotBreakInviteAcceptance_UnderLocaleMismatch()
    {
        // Owner invites the member-to-be.
        var household = await SignInToHouseholdAsync(Page, UniqueEmail("owner"));
        var memberEmail = UniqueEmail("es-member");
        var token = await household.InviteAsync(memberEmail);
        await Mailpit.ClearAsync();

        // The member picks Español pre-auth (device choice) and signs in — the choice is adopted
        // into their account (PREFS-1), so their SERVER locale is now "es".
        await using var memberCtx = await Browser.NewContextAsync(ContextOptions());
        var memberPage = await memberCtx.NewPageAsync();
        var login = new LoginPage(memberPage);
        await login.GotoAsync();
        await Expect(login.SendOtp).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await memberPage.GetByTestId("language-switcher").SelectOptionAsync("es"); // full reload
        await Expect(login.SendOtp).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await memberPage.RunAndWaitForResponseAsync(
            () => SignInAsync(memberPage, memberEmail),
            r => r.Url.EndsWith("/api/auth/locale") && r.Request.Method == "PUT",
            new() { Timeout = 60_000 });

        // Force the UX-1 cold start: wipe the device locale cache, then open the invite link as a
        // FRESH page load. The app boots in English while the account says Spanish → the reconcile
        // reloads once, and must land back on /join with the token intact.
        await memberPage.EvaluateAsync("localStorage.removeItem('app_culture')");
        var join = new JoinPage(memberPage);
        await join.GotoWithTokenAsync(token);

        // The reload happened (device cache re-flipped to es) and did NOT bounce off /join…
        await memberPage.WaitForFunctionAsync(
            "() => localStorage.getItem('app_culture') === 'es'",
            null, new() { Timeout = 30_000 });
        Assert.That(new Uri(memberPage.Url).AbsolutePath, Does.Contain("/join"));

        // …and the acceptance completed: success state shown, roster actually grew to 2.
        await Expect(join.Success).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await household.GotoAsync();
        await Expect(household.MemberRows).ToHaveCountAsync(2, new() { Timeout = 30_000 });
    }
}
