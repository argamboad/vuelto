using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Perezosoft.E2E.Tests.Pages;

namespace Perezosoft.E2E.Tests;

/// <summary>
/// Membership lifecycle journey (E2E-5) — the destructive flows, safe here because every test
/// creates fresh throwaway users: ownership transfer, member leave, sole-owner dissolve, and
/// account deletion. Leave/dissolve re-home the user into a fresh tenant-of-one (TenantService),
/// which is what the UI assertions pin. Maps to QA-HH-05/07/08 and QA-SET-07 (delete account).
/// </summary>
[TestFixture]
public class MembershipLifecycleTests : E2ETestBase
{
    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    [Test]
    public async Task Owner_Transfers_Ownership()
    {
        var (ownerEmail, memberEmail) = (UniqueEmail("owner"), UniqueEmail("member"));
        var household = await SignInToHouseholdAsync(Page, ownerEmail);

        await using var memberCtx = await Browser.NewContextAsync(ContextOptions());
        var memberPage = await memberCtx.NewPageAsync();
        await InviteAndJoinAsync(household, memberPage, memberEmail);

        await household.GotoAsync();
        await Expect(household.MemberRow(memberEmail)).ToBeVisibleAsync(Slow);

        // The transfer select lists the other member by email (no display name set).
        await household.TransferSelect.SelectOptionAsync(new SelectOptionValue { Label = memberEmail });
        await household.TransferSubmit.ClickAsync();

        // Badges swap: the member is now the owner, the ex-owner a plain member...
        await Expect(household.RoleBadge(memberEmail))
            .ToHaveAttributeAsync("data-role", "owner", new() { Timeout = 15_000 });
        await Expect(household.RoleBadge(ownerEmail)).ToHaveAttributeAsync("data-role", "member");

        // ...and the ex-owner's page now shows member-level controls only.
        await household.GotoAsync();
        await Expect(household.MemberRow(memberEmail)).ToBeVisibleAsync(Slow);
        await Expect(household.RenameInput).Not.ToBeVisibleAsync();
        await Expect(household.TransferSelect).Not.ToBeVisibleAsync();
    }

    [Test]
    public async Task Member_Leaves_The_Household()
    {
        var (ownerEmail, memberEmail) = (UniqueEmail("owner"), UniqueEmail("member"));
        var household = await SignInToHouseholdAsync(Page, ownerEmail);

        await using var memberCtx = await Browser.NewContextAsync(ContextOptions());
        var memberPage = await memberCtx.NewPageAsync();
        await InviteAndJoinAsync(household, memberPage, memberEmail);

        var memberView = new HouseholdPage(memberPage);
        await memberView.GotoAsync();
        await Expect(memberView.MemberRow(ownerEmail)).ToBeVisibleAsync(Slow);

        memberPage.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        await memberView.LeaveHousehold.ClickAsync();
        await Expect(memberPage).ToHaveURLAsync(new Regex(".*/$"), new() { Timeout = 30_000 });

        // The leaver is re-homed into a fresh tenant-of-one where they are the owner.
        await memberView.GotoAsync();
        await Expect(memberView.MemberRow(memberEmail)).ToBeVisibleAsync(Slow);
        await Expect(memberView.MemberRows).ToHaveCountAsync(1);
        await Expect(memberView.RoleBadge(memberEmail)).ToHaveAttributeAsync("data-role", "owner");

        // The old household no longer lists them.
        await household.GotoAsync();
        await Expect(household.MemberRow(ownerEmail)).ToBeVisibleAsync(Slow);
        await Expect(household.MemberRow(memberEmail)).Not.ToBeVisibleAsync();
    }

    [Test]
    public async Task Sole_Owner_Dissolves_The_Household()
    {
        var household = await SignInToHouseholdAsync(Page, UniqueEmail("dissolver"));

        // Give the doomed household a recognizable name so we can prove the re-homed one is new.
        var doomedName = $"Doomed-{Guid.NewGuid():N}";
        await household.RenameAsync(doomedName);
        await Expect(household.RenameInput).ToHaveValueAsync(doomedName, new() { Timeout = 15_000 });

        Page.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        await household.LeaveDissolve.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/$"), new() { Timeout = 30_000 });

        // Re-homed into a fresh tenant-of-one: still owner, but not of the doomed household.
        await household.GotoAsync();
        await Expect(household.RenameInput).ToBeVisibleAsync(Slow);
        await Expect(household.RenameInput).Not.ToHaveValueAsync(doomedName);
        await Expect(household.MemberRows).ToHaveCountAsync(1);
    }

    [Test]
    public async Task Member_Joins_By_Pasting_The_Invite_Code()
    {
        var (ownerEmail, memberEmail) = (UniqueEmail("owner"), UniqueEmail("member"));
        var household = await SignInToHouseholdAsync(Page, ownerEmail);
        var token = await household.InviteAsync(memberEmail);

        await Mailpit.ClearAsync();
        await using var memberCtx = await Browser.NewContextAsync(ContextOptions());
        var memberPage = await memberCtx.NewPageAsync();
        await SignInAsync(memberPage, memberEmail);

        // NATIVE-4b (parity gap G5): a native app has no address bar for the emailed
        // /join?token=… link, so the member opens Join from the app and pastes the code.
        var join = new JoinPage(memberPage);
        await join.JoinWithCodeAsync(token);
        await Expect(join.Success).ToBeVisibleAsync(Slow);

        var memberView = new HouseholdPage(memberPage);
        await memberView.GotoAsync();
        await Expect(memberView.MemberRow(ownerEmail)).ToBeVisibleAsync(Slow);
        await Expect(memberView.MemberRow(memberEmail)).ToBeVisibleAsync();
    }

    [Test]
    public async Task Pasting_An_Invalid_Code_Shows_An_Inline_Error()
    {
        await SignInToHouseholdAsync(Page, UniqueEmail("badcode"));

        var join = new JoinPage(Page);
        await join.JoinWithCodeAsync("not-a-real-invite-code");

        // The error is inline and the form stays usable for a retry.
        await Expect(join.CodeError).ToBeVisibleAsync(Slow);
        await Expect(join.CodeInput).ToBeVisibleAsync();
    }

    [Test]
    public async Task Member_Deletes_Their_Account()
    {
        var (ownerEmail, memberEmail) = (UniqueEmail("owner"), UniqueEmail("member"));
        var household = await SignInToHouseholdAsync(Page, ownerEmail);

        await using var memberCtx = await Browser.NewContextAsync(ContextOptions());
        var memberPage = await memberCtx.NewPageAsync();
        await InviteAndJoinAsync(household, memberPage, memberEmail);

        var settings = new SettingsPage(memberPage);
        await settings.GotoAsync();
        await Expect(settings.DeleteAccount).ToBeVisibleAsync(Slow);

        memberPage.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        await settings.DeleteAccount.ClickAsync();

        // Erasure signs the user out and lands on the login page.
        await Expect(memberPage).ToHaveURLAsync(new Regex(".*/login"), new() { Timeout = 30_000 });
        await Expect(new LoginPage(memberPage).Email).ToBeVisibleAsync(Slow);

        // The owner's roster no longer lists the erased member.
        await household.GotoAsync();
        await Expect(household.MemberRow(ownerEmail)).ToBeVisibleAsync(Slow);
        await Expect(household.MemberRow(memberEmail)).Not.ToBeVisibleAsync();
    }
}
