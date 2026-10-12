using Microsoft.Playwright;
using Vuelto.E2E.Tests.Pages;

namespace Vuelto.E2E.Tests;

/// <summary>
/// Notification center &amp; preferences journey (E2E-3): the bell's empty state for a fresh user and
/// the delivery-preferences round-trip (NOTIFY-1/2, ADR-013). Notification creation/mark-read stays
/// at the integration layer (tests/Api.Tests/Notify) — the only production producer is the billing
/// notifier, which isn't browser-triggerable. Maps to QA-NOTIF-01 (empty state) and QA-NOTIF-03;
/// QA-NOTIF-02 (mark read) stays manual for the same reason.
/// </summary>
[TestFixture]
public class NotificationJourneyTests : E2ETestBase
{
    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    [Test]
    public async Task Bell_Shows_The_Empty_State_For_A_Fresh_User()
    {
        await Mailpit.ClearAsync();
        await SignInAsync(Page, UniqueEmail("fresh"));

        await Expect(Page.GetByTestId("notif-bell")).ToBeVisibleAsync(Slow);
        await Expect(Page.GetByTestId("notif-count")).Not.ToBeVisibleAsync(); // no unread badge

        await Page.GetByTestId("notif-bell").ClickAsync();
        await Expect(Page.GetByTestId("notif-panel")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("notif-empty")).ToBeVisibleAsync(new() { Timeout = 15_000 });
    }

    [Test]
    public async Task Delivery_Preferences_RoundTrip()
    {
        await Mailpit.ClearAsync();
        await SignInAsync(Page, UniqueEmail("prefs"));

        var settings = new SettingsPage(Page);
        await settings.GotoAsync();

        // Both channels default on (NOTIFY-2).
        await Expect(settings.NotifPrefInApp).ToBeCheckedAsync(new() { Timeout = 30_000 });
        await Expect(settings.NotifPrefEmail).ToBeCheckedAsync();

        // Turn email off; the toggle disables while saving and re-enables when the PUT lands.
        await settings.NotifPrefEmail.ClickAsync();
        await Expect(settings.NotifPrefEmail).Not.ToBeCheckedAsync();
        await Expect(settings.NotifPrefEmail).ToBeEnabledAsync(new() { Timeout = 15_000 });

        // Survives a full reload — the preference was persisted, not just local state.
        await settings.GotoAsync();
        await Expect(settings.NotifPrefInApp).ToBeCheckedAsync(new() { Timeout = 30_000 });
        await Expect(settings.NotifPrefEmail).Not.ToBeCheckedAsync();
    }

    [Test]
    public async Task Preferences_Are_PerUser()
    {
        await Mailpit.ClearAsync();
        await SignInAsync(Page, UniqueEmail("usera"));

        var settingsA = new SettingsPage(Page);
        await settingsA.GotoAsync();
        await Expect(settingsA.NotifPrefEmail).ToBeCheckedAsync(new() { Timeout = 30_000 });
        await settingsA.NotifPrefEmail.ClickAsync();
        await Expect(settingsA.NotifPrefEmail).Not.ToBeCheckedAsync();
        await Expect(settingsA.NotifPrefEmail).ToBeEnabledAsync(new() { Timeout = 15_000 });

        // A fresh user in their own context still gets the defaults.
        await using var ctxB = await Browser.NewContextAsync(ContextOptions());
        var pageB = await ctxB.NewPageAsync();
        await SignInAsync(pageB, UniqueEmail("userb"));

        var settingsB = new SettingsPage(pageB);
        await settingsB.GotoAsync();
        await Expect(settingsB.NotifPrefInApp).ToBeCheckedAsync(new() { Timeout = 30_000 });
        await Expect(settingsB.NotifPrefEmail).ToBeCheckedAsync();
    }
}
