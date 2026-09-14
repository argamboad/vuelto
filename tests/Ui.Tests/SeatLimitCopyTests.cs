using System.Net;
using Bunit;
using Xunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;

namespace Vuelto.Ui.Tests;

/// <summary>
/// GATES-1 (ADR-027): the upgrade pitch leaks well past the billing page. Two places tell a user to buy
/// a bigger plan when a household runs out of seats, and with billing off that plan cannot be bought —
/// the deployment has no provider account behind it. Both must say "this is full" instead of "pay us".
/// <para>
/// The deterministic localizer echoes resource KEYS, so these assert on which key each branch chose,
/// which is exactly the decision under test (the wording itself lives in the resx files, at EN/ES parity).
/// </para>
/// </summary>
public class SeatLimitCopyTests : ComponentTestBase
{
    /// <summary>A roster load that renders the invite form, then a seat-capped invite attempt.</summary>
    private void HouseholdAtCapacity()
    {
        Http.On(HttpMethod.Get, "/api/household",
            """{"id":"11111111-1111-1111-1111-111111111111","name":"Test Household","my_role":"owner","members":[]}""");
        Http.On(HttpMethod.Get, "/api/household/invitations", "[]");
        Http.On(HttpMethod.Post, "/api/household/invitations", "{}", HttpStatusCode.PaymentRequired);
    }

    private async Task<IRenderedComponent<Household>> InviteAtCapacityAsync(bool billing)
    {
        StubFeatures(billing);
        HouseholdAtCapacity();
        await SignInAsync();

        var page = Render<Household>();
        page.Find("[data-testid=invite-email]").Change("friend@example.com");
        await page.Find("[data-testid=invite-send]").ClickAsync(new());
        return page;
    }

    [Fact]
    public async Task Invite_OverTheSeatLimit_OffersNoUpgrade_WhenBillingIsOff()
    {
        var page = await InviteAtCapacityAsync(billing: false);

        Assert.Contains("Household_ErrSeatLimitNoBilling", page.Markup);
        Assert.DoesNotContain("Household_ErrSeatLimit[", page.Markup); // not the upgrade wording
    }

    [Fact]
    public async Task Invite_OverTheSeatLimit_StillOffersTheUpgrade_WhenBillingIsOn()
    {
        var page = await InviteAtCapacityAsync(billing: true);

        Assert.Contains("Household_ErrSeatLimit", page.Markup);
        Assert.DoesNotContain("Household_ErrSeatLimitNoBilling", page.Markup);
    }

    [Fact]
    public async Task Join_FullHousehold_DoesNotTellTheInviteeToAskForAnUpgrade_WhenBillingIsOff()
    {
        StubFeatures(billing: false);
        Http.On(HttpMethod.Post, "/api/household/invitations/accept", "{}", HttpStatusCode.PaymentRequired);
        await SignInAsync();

        Services.GetRequiredService<NavigationManager>().NavigateTo("/join?token=any-token");
        var page = Render<Join>();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid=join-household-full]")));
        Assert.Contains("Join_FullBodyNoBilling", page.Markup);
    }

    [Fact]
    public async Task Join_FullHousehold_KeepsTheUpgradeHint_WhenBillingIsOn()
    {
        StubFeatures(billing: true);
        Http.On(HttpMethod.Post, "/api/household/invitations/accept", "{}", HttpStatusCode.PaymentRequired);
        await SignInAsync();

        Services.GetRequiredService<NavigationManager>().NavigateTo("/join?token=any-token");
        var page = Render<Join>();

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid=join-household-full]")));
        Assert.Contains("Join_FullBody", page.Markup);
        Assert.DoesNotContain("Join_FullBodyNoBilling", page.Markup);
    }
}
