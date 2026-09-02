using Microsoft.Playwright;
using Perezosoft.E2E.Tests.Pages;

namespace Perezosoft.E2E.Tests;

/// <summary>
/// Staff announcement journey (ADMIN-3): platform staff sends an announcement from the admin
/// console; the target user's bell shows it, and mark-read clears the badge. This is the first
/// browser-triggerable notification producer, so it also closes the bell list/mark-read gap
/// (QA-NOTIF-01 full + QA-NOTIF-02) and gives the admin console its first E2E coverage.
/// Requires the API to run with <c>--Admin:StaffEmails:0=e2e-staff@example.com</c> (see README).
/// </summary>
[TestFixture]
public class AnnouncementJourneyTests : E2ETestBase
{
    // Must match the Admin:StaffEmails entry the API is started with (README / ci.yml).
    private const string StaffEmail = "e2e-staff@example.com";

    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    [Test]
    public async Task Staff_Announcement_Reaches_The_Members_Bell_And_MarkRead_Clears_It()
    {
        // The target: a fresh owner whose household gets a unique name so staff can find it.
        var ownerEmail = UniqueEmail("announce-target");
        var householdName = $"E2E-Announce-{Guid.NewGuid():N}";
        var household = await SignInToHouseholdAsync(Page, ownerEmail);
        await household.RenameAsync(householdName);
        await Expect(household.RenameInput).ToHaveValueAsync(householdName, new() { Timeout = 15_000 });
        await Expect(Page.GetByTestId("notif-count")).Not.ToBeVisibleAsync(); // no unread yet

        // Staff signs in on their own context and sends the announcement.
        await using var staffCtx = await Browser.NewContextAsync(ContextOptions());
        var staffPage = await staffCtx.NewPageAsync();
        await Mailpit.ClearAsync();
        await SignInAsync(staffPage, StaffEmail);

        var admin = new AdminConsolePage(staffPage);
        await admin.GotoAsync();
        await Expect(admin.TenantRow(householdName)).ToBeVisibleAsync(Slow);
        await admin.TenantRow(householdName).ClickAsync();

        staffPage.Dialog += async (_, dialog) => await dialog.AcceptAsync();
        var title = $"Maintenance {Guid.NewGuid():N}";
        await admin.AnnounceAsync(title, "We will be down tonight for maintenance.");
        await Expect(admin.Status).ToBeVisibleAsync(new() { Timeout = 15_000 });

        // The owner's bell: badge shows the unread announcement (reload — the badge polls every 60s).
        await Page.ReloadAsync();
        await Expect(Page.GetByTestId("notif-count")).ToBeVisibleAsync(Slow);
        await Expect(Page.GetByTestId("notif-count")).ToHaveTextAsync("1");

        await Page.GetByTestId("notif-bell").ClickAsync();
        var item = Page.GetByTestId("notif-item").Filter(new() { HasText = title });
        await Expect(item).ToBeVisibleAsync(new() { Timeout = 15_000 });

        // Mark-read: clicking the item clears the badge.
        await item.ClickAsync();
        await Expect(Page.GetByTestId("notif-count")).Not.ToBeVisibleAsync(new() { Timeout = 15_000 });
    }

    [Test]
    public async Task NonStaff_Gets_The_Forbidden_State()
    {
        await Mailpit.ClearAsync();
        await SignInAsync(Page, UniqueEmail("not-staff"));

        var admin = new AdminConsolePage(Page);
        await admin.GotoAsync();
        await Expect(admin.Forbidden).ToBeVisibleAsync(Slow);
        await Expect(admin.TenantRows).ToHaveCountAsync(0);
    }
}
