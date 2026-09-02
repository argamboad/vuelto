using Microsoft.Playwright;
using Perezosoft.E2E.Tests.Pages;

namespace Perezosoft.E2E.Tests;

/// <summary>
/// RBAC roster journey (E2E-1): invite → join via token → promote/demote → remove, plus the
/// permission-aware roster for non-owners (ADR-009). The API's 403 gates are integration-tested
/// (Rbac/*, RbacForbiddenIntegrationTests); this verifies the UI reflects the caller's role.
/// Role assertions use the data-role attribute, not localized badge text.
/// </summary>
[TestFixture]
public class RosterJourneyTests : E2ETestBase
{
    // Blazor WASM boots the .NET runtime on first hit — be generous.
    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    [Test]
    public async Task Owner_Invites_And_Member_Joins_Via_Token()
    {
        var (ownerEmail, memberEmail) = (UniqueEmail("owner"), UniqueEmail("member"));
        var household = await SignInToHouseholdAsync(Page, ownerEmail);

        var token = await household.InviteAsync(memberEmail);
        await Expect(household.PendingRow(memberEmail)).ToBeVisibleAsync();

        await using var memberCtx = await Browser.NewContextAsync(ContextOptions());
        var memberPage = await memberCtx.NewPageAsync();
        await Mailpit.ClearAsync(); // drop the invite email so the OTP poll can't misread it
        await SignInAsync(memberPage, memberEmail);

        var join = new JoinPage(memberPage);
        await join.GotoWithTokenAsync(token);
        await Expect(join.Success).ToBeVisibleAsync(Slow);

        // The owner's reloaded roster shows the new member with the member role.
        await household.GotoAsync();
        await Expect(household.MemberRow(memberEmail)).ToBeVisibleAsync(Slow);
        await Expect(household.RoleBadge(memberEmail)).ToHaveAttributeAsync("data-role", "member");
        await Expect(household.PendingRow(memberEmail)).Not.ToBeVisibleAsync();
    }

    [Test]
    public async Task Owner_Promotes_And_Demotes_Member()
    {
        var (ownerEmail, memberEmail) = (UniqueEmail("owner"), UniqueEmail("member"));
        var household = await SignInToHouseholdAsync(Page, ownerEmail);

        await using var memberCtx = await Browser.NewContextAsync(ContextOptions());
        var memberPage = await memberCtx.NewPageAsync();
        await InviteAndJoinAsync(household, memberPage, memberEmail);

        await household.GotoAsync();
        await Expect(household.MemberRow(memberEmail)).ToBeVisibleAsync(Slow);

        await household.Promote(memberEmail).ClickAsync();
        await Expect(household.RoleBadge(memberEmail))
            .ToHaveAttributeAsync("data-role", "admin", new() { Timeout = 15_000 });

        await household.Demote(memberEmail).ClickAsync();
        await Expect(household.RoleBadge(memberEmail))
            .ToHaveAttributeAsync("data-role", "member", new() { Timeout = 15_000 });
    }

    [Test]
    public async Task Roster_Is_PermissionAware_For_NonOwner()
    {
        var (ownerEmail, memberEmail) = (UniqueEmail("owner"), UniqueEmail("member"));
        var household = await SignInToHouseholdAsync(Page, ownerEmail);

        await using var memberCtx = await Browser.NewContextAsync(ContextOptions());
        var memberPage = await memberCtx.NewPageAsync();
        await InviteAndJoinAsync(household, memberPage, memberEmail);

        // As a plain member: no rename, no invite form, no role/remove controls anywhere.
        var memberView = new HouseholdPage(memberPage);
        await memberView.GotoAsync();
        await Expect(memberView.MemberRow(ownerEmail)).ToBeVisibleAsync(Slow); // roster loaded
        await Expect(memberView.RenameInput).Not.ToBeVisibleAsync();
        await Expect(memberView.InviteEmail).Not.ToBeVisibleAsync();
        await Expect(memberPage.GetByTestId("member-promote")).ToHaveCountAsync(0);
        await Expect(memberPage.GetByTestId("member-demote")).ToHaveCountAsync(0);
        await Expect(memberPage.GetByTestId("member-remove")).ToHaveCountAsync(0);

        // Promoted to admin: invite (ManageMembers) appears, but promote/demote (ManageRoles —
        // owner only) still doesn't. (No remove buttons either: the only other row is the owner.)
        await household.GotoAsync();
        await Expect(household.MemberRow(memberEmail)).ToBeVisibleAsync(Slow);
        await household.Promote(memberEmail).ClickAsync();
        await Expect(household.RoleBadge(memberEmail))
            .ToHaveAttributeAsync("data-role", "admin", new() { Timeout = 15_000 });

        await memberView.GotoAsync();
        await Expect(memberView.InviteEmail).ToBeVisibleAsync(Slow);
        await Expect(memberView.RenameInput).ToBeVisibleAsync();
        await Expect(memberPage.GetByTestId("member-promote")).ToHaveCountAsync(0);
        await Expect(memberPage.GetByTestId("member-demote")).ToHaveCountAsync(0);
        await Expect(memberPage.GetByTestId("member-remove")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Owner_Removes_Member()
    {
        var (ownerEmail, memberEmail) = (UniqueEmail("owner"), UniqueEmail("member"));
        var household = await SignInToHouseholdAsync(Page, ownerEmail);

        await using var memberCtx = await Browser.NewContextAsync(ContextOptions());
        var memberPage = await memberCtx.NewPageAsync();
        await InviteAndJoinAsync(household, memberPage, memberEmail);

        await household.GotoAsync();
        await Expect(household.MemberRow(memberEmail)).ToBeVisibleAsync(Slow);

        // Removal asks for confirmation via window.confirm — accept it.
        Page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        await household.Remove(memberEmail).ClickAsync();

        await Expect(household.MemberRow(memberEmail))
            .Not.ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(household.MemberRow(ownerEmail)).ToBeVisibleAsync();
    }

}
