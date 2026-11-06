using Microsoft.Playwright;
using Vuelto.E2E.Tests.Pages;
using Perezosoft.Testing.E2E;

namespace Vuelto.E2E.Tests;

/// <summary>
/// Base for this repo's journeys: the packaged one (<see cref="PlatformE2ETestBase"/>, Pkg 5.1d — the browser, the
/// hosts, the billing webhook, the console report) plus what knows this app's pages: sign-in, landing on the
/// household, the invite-and-join round trip, over the page objects in <c>Pages/</c>.
/// </summary>
public abstract class E2ETestBase : PlatformE2ETestBase
{
    /// <summary>
    /// The Free plan's seat cap, as the journeys see it in the UI. The ONE copy in this project (v4 audit T47,
    /// R88) — the browser tests cannot reference the API's assemblies, so
    /// <c>CatalogNumbersTests</c> in Api.Tests reads this line and fails when it differs from <c>PlanCatalog</c>.
    /// </summary>
    protected const int FreePlanSeatLimit = 5;

    /// <summary>OTP sign-in on the given page/context and wait for the app shell.</summary>
    protected static async Task SignInAsync(IPage page, string email)
    {
        var login = new LoginPage(page);
        await login.GotoAsync();
        await Assertions.Expect(login.Email).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await login.SignInWithOtpAsync(email);
        await Assertions.Expect(page.GetByTestId("user-menu")).ToBeVisibleAsync(new() { Timeout = 30_000 });
    }

    /// <summary>Clears Mailpit, signs the user in, and lands on the Household page.</summary>
    protected static async Task<HouseholdPage> SignInToHouseholdAsync(IPage page, string email)
    {
        await Mailpit.ClearAsync();
        await SignInAsync(page, email);
        var household = new HouseholdPage(page);
        await household.GotoAsync();
        return household;
    }

    /// <summary>
    /// Owner invites <paramref name="memberEmail"/>; the member signs in on
    /// <paramref name="memberPage"/> and accepts via the revealed token.
    /// </summary>
    protected static async Task InviteAndJoinAsync(HouseholdPage owner, IPage memberPage, string memberEmail)
    {
        var token = await owner.InviteAsync(memberEmail);

        // Drop the invitation email so the member's OTP poll can't misread it.
        await Mailpit.ClearAsync();
        await SignInAsync(memberPage, memberEmail);

        var join = new JoinPage(memberPage);
        await join.GotoWithTokenAsync(token);
        await Assertions.Expect(join.Success).ToBeVisibleAsync(new() { Timeout = 30_000 });
    }
}
