using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Vuelto.E2E.Tests.Pages;

namespace Vuelto.E2E.Tests;

/// <summary>
/// The shipped default, end to end (v4 audit T48, BILL-10, R147): <c>Billing:Enabled</c> unset and a signup
/// green list with one domain on it. Every other journey runs with billing ON and no green list, so until
/// these four nothing in a browser ever saw the posture a new app launches with; QA-GATE-01/02/04/05 were
/// manual only. They run in their own lane — <c>pwsh tools/e2e.ps1 -Gates</c> locally, the "gates off" step in
/// CI — against an API started with exactly that configuration, and the default lane leaves them out
/// (<c>TestCategory!=Gate</c>). QA-GATE-03 (billing on, the surface whole) is what every other journey proves.
/// </summary>
[TestFixture]
[Category("Gate")]
public class GateJourneyTests : E2ETestBase
{
    /// <summary>
    /// The one domain on the lane's green list. The lane's two launchers (tools/e2e.ps1 and the CI step) set
    /// <c>Signup__AllowedDomains__0</c> to this literal; <c>EnforcementGateTests.GateLane_…</c> holds them to it.
    /// A domain, not an address, so every journey founds a household of its own: with one listed address the
    /// journey that fills the household's seats would fill them for the other three.
    /// </summary>
    public const string ListedDomain = "listed.example.com";

    /// <summary>A fresh address the green list admits. Everything <see cref="E2ETestBase.UniqueEmail"/> makes is a stranger.</summary>
    private static string ListedOwner() => $"e2e-owner-{Guid.NewGuid():N}@{ListedDomain}";

    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    // ── Billing off (GATES-1, ADR-027) ───────────────────────────────────────────────────────────────

    [Test]
    [Category("GateBilling")]
    public async Task BillingOff_TheSurfaceDoesNotExist_NoLink_NoPage_ApiAnswers404()
    {
        // QA-GATE-01. A listed owner founds a household — only a listed address may create one in this lane.
        var household = await SignInToHouseholdAsync(Page, ListedOwner());
        await Expect(household.RenameInput).ToBeVisibleAsync(Slow);

        // No Billing link in the user menu (Household is there, so the menu itself rendered). In this app the
        // household and billing links live inside the user menu (SKIN-4), so open it first.
        await Page.GetByTestId("user-menu").ClickAsync();
        await Expect(Page.GetByTestId("nav-household")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("nav-billing")).ToHaveCountAsync(0);

        // A typed /billing leaves before rendering anything: the page sends the visitor home.
        await BlazorBoot.GotoAsync(Page, "/billing");
        await Expect(Page).ToHaveURLAsync(new Regex(@"^https?://[^/]+/?(\?.*)?$"), new() { Timeout = 30_000 }); // the root, not /billing
        await Expect(Page.GetByTestId("billing-plan")).ToHaveCountAsync(0);

        // The routes are GONE, not protected: 404 anonymously, and the provider webhook is gated too.
        using var api = NewApiClient();
        var billing = await api.GetAsync("/api/billing");
        Assert.That((int)billing.StatusCode, Is.EqualTo(404), "/api/billing should not exist with billing off");
        var webhook = await api.PostAsync("/api/billing/webhook", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.That((int)webhook.StatusCode, Is.EqualTo(404), "/api/billing/webhook should not exist with billing off");
    }

    [Test]
    [Category("GateBilling")]
    public async Task BillingOff_AFullHousehold_SaysFull_AndNeverOffersAnUpgrade()
    {
        // QA-GATE-02. Seats = members + pending invitations (BILLING-5), so pending invites fill the Free cap.
        var household = await SignInToHouseholdAsync(Page, ListedOwner());
        for (var i = 1; i <= FreePlanSeatLimit - 1; i++)
        {
            var email = UniqueEmail($"gate-invitee{i}");
            await household.InviteAsync(email);
            await Expect(household.PendingRow(email)).ToBeVisibleAsync(new() { Timeout = 15_000 });
        }

        // One more: the household is full, and the copy is the no-billing one — nothing to upgrade to.
        await household.InviteEmail.FillAsync(UniqueEmail("gate-overlimit"));
        await household.InviteEmail.BlurAsync();
        await household.InviteSend.ClickAsync();
        await Expect(household.Status).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(household.Status).ToContainTextAsync("household is full"); // Household_ErrSeatLimitNoBilling (EN)
        await Expect(household.Status).Not.ToContainTextAsync("Upgrade");
    }

    // ── Signup green list (GATES-2, ADR-027) ──────────────────────────────────────────────────────────

    [Test]
    [Category("GateSignup")]
    public async Task GreenList_AStrangerIsRefusedAtRedemption_WithTheRightWords()
    {
        // QA-GATE-04. The gate runs when the code is REDEEMED, not when it is issued — refusing at issue would
        // tell a caller whether an address has an account — so the send succeeds and the correct code is refused.
        await Mailpit.ClearAsync();
        var login = new LoginPage(Page);
        await login.GotoAsync();
        await Expect(login.Email).ToBeVisibleAsync(Slow);

        var stranger = UniqueEmail("gate-stranger");
        await login.SignInWithOtpAsync(stranger); // fills the real code from Mailpit and submits it

        await Expect(login.OtpError).ToBeVisibleAsync(Slow);
        await Expect(login.OtpError).ToContainTextAsync("private testing"); // Login_ErrSignupNotAllowed (EN), not "incorrect or expired"
        await Expect(Page.GetByTestId("sign-out")).ToHaveCountAsync(0);
    }

    [Test]
    [Category("GateSignup")]
    public async Task GreenList_AListedOwnerFoundsAHousehold_AndAnUnlistedInviteeMayJoinIt()
    {
        // QA-GATE-05. The list decides who may FOUND a household; inside a listed owner's household,
        // membership is the owner's business — an invitation from them admits an address the list does not name.
        var owner = await SignInToHouseholdAsync(Page, ListedOwner());
        await Expect(owner.RenameInput).ToBeVisibleAsync(Slow);

        var memberContext = await Browser.NewContextAsync(ContextOptions());
        var memberPage = await memberContext.NewPageAsync();
        BlazorBoot.Watch(memberPage);
        try
        {
            var invitee = UniqueEmail("gate-invitee");
            await InviteAndJoinAsync(owner, memberPage, invitee); // signs the unlisted invitee in, then redeems the token
            await owner.GotoAsync(); // the roster is read on load
            await Expect(owner.MemberRow(invitee)).ToBeVisibleAsync(Slow);
        }
        finally
        {
            await memberContext.CloseAsync();
        }
    }
}
