using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Shared.Ui.Components;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>
/// v3 audit LB-UI-9 (bell double-decrement) + UX-5 (raw billing tokens), T42.
/// </summary>
public class NotifyBillingTests : ComponentTestBase
{
    private const string ItemA = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string ItemB = "bbbbbbbb-0000-0000-0000-000000000002";

    [Fact]
    public async Task Bell_DoubleClickUnread_DecrementsOnce_NotTwice()
    {
        await SignInAsync();
        static string Notif(string id, string title) =>
            $$"""{"id":"{{id}}","kind":"x","title":"{{title}}","body":"","read_at":null,"created_at":"2026-01-01T00:00:00Z"}""";
        Http.On(HttpMethod.Get, "/api/notifications/unread-count", """{"count":2}""");
        Http.On(HttpMethod.Get, "/api/notifications", $"[{Notif(ItemA, "A")},{Notif(ItemB, "B")}]");
        // The read POST hangs, so a second click lands while the first is still in flight (the race).
        var releaseRead = Http.OnGated(HttpMethod.Post, $"/api/notifications/{ItemA}/read");

        var cut = Render<NotificationBell>();
        cut.Find("[data-testid='notif-bell']").Click(); // open the dropdown → loads the list

        // Re-find before each click: the optimistic mark re-renders the item (new handler id). The second
        // click lands while the first read POST is still gated — the race that used to double-decrement.
        cut.FindAll("[data-testid='notif-item']")[0].Click();
        cut.FindAll("[data-testid='notif-item']")[0].Click();
        releaseRead();  // let the first POST complete

        // Two unread → reading ONE leaves exactly one unread (a double-decrement would show 0).
        Assert.Equal("1", cut.Find("[data-testid='notif-count']").TextContent.Trim());
    }

    [Theory]
    [InlineData(30, "Notif_JustNow")] // < 1 minute → the localized "just now"
    [InlineData(5 * 60, "5m")]        // minutes bucket
    [InlineData(3 * 3600, "3h")]      // hours bucket
    [InlineData(2 * 86400, "2d")]     // days bucket
    public async Task Bell_RendersRelativeAge_PerBucket(int ageSeconds, string expected)
    {
        // v3 TB-UI backfill (T45c): the Ago buckets — just-now / m / h / d — rendered through the
        // real component (the method is private; the list item's timestamp line is the contract).
        await SignInAsync();
        var created = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(ageSeconds);
        Http.On(HttpMethod.Get, "/api/notifications/unread-count", """{"count":1}""");
        Http.On(HttpMethod.Get, "/api/notifications",
            $$"""[{"id":"{{ItemA}}","kind":"x","title":"T","body":"","read_at":null,"created_at":"{{created:O}}"}]""");

        var cut = Render<NotificationBell>();
        cut.Find("[data-testid='notif-bell']").Click();

        Assert.Contains(expected, cut.Find("[data-testid='notif-item']").TextContent);
    }

    [Fact]
    public async Task Bell_RendersOldNotifications_AsADate_NotRelative()
    {
        // ≥ 7 days falls back to a real date — "43d" would read like an error.
        await SignInAsync();
        var created = DateTimeOffset.UtcNow - TimeSpan.FromDays(10);
        Http.On(HttpMethod.Get, "/api/notifications/unread-count", """{"count":1}""");
        Http.On(HttpMethod.Get, "/api/notifications",
            $$"""[{"id":"{{ItemA}}","kind":"x","title":"T","body":"","read_at":null,"created_at":"{{created:O}}"}]""");

        var cut = Render<NotificationBell>();
        cut.Find("[data-testid='notif-bell']").Click();

        Assert.Contains(created.LocalDateTime.ToString("d"), cut.Find("[data-testid='notif-item']").TextContent);
    }

    [Fact]
    public async Task Billing_RendersLocalizedPlanAndStatus_NotRawTokens()
    {
        await SignInAsync();
        StubFeatures(); // GATES-1: the page leaves immediately when billing is gated off
        Http.On(HttpMethod.Get, "/api/billing", """{"plan_key":"free","status":"active"}""");

        var cut = Render<Billing>();
        cut.WaitForElement("[data-testid='billing-plan']");

        // The fake localizer echoes the key, proving the token is looked up (not rendered raw).
        Assert.Equal("Plan_free", cut.Find("[data-testid='billing-plan']").TextContent.Trim());
        Assert.Equal("BillingStatus_active", cut.Find("[data-testid='billing-status']").TextContent.Trim());
    }

    [Fact]
    public async Task Billing_ReturnFromCheckout_Success_ShowsTheBanner_AndRefetchesUntilThePlanFlips()
    {
        // The hosted checkout sends the user back to /billing/success — the same page, with a banner. The webhook
        // that flips the plan lands a moment later, so the page refetches on its own (no manual reload).
        await SignInAsync();
        StubFeatures(); // GATES-1: the page leaves immediately when billing is gated off
        Http.On(HttpMethod.Get, "/api/billing", """{"plan_key":"free","status":"active"}""");
        Services.GetRequiredService<NavigationManager>().NavigateTo("/billing/success");

        var cut = Render<Billing>(p => p.Add(x => x.RefreshDelayMs, 10));
        cut.WaitForElement("[data-testid='billing-plan']");
        Assert.Contains("Billing_CheckoutSuccess", cut.Find("[data-testid='billing-checkout-success']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='billing-checkout-cancel']"));

        StubFeatures(); // GATES-1: the page leaves immediately when billing is gated off

        Http.On(HttpMethod.Get, "/api/billing", """{"plan_key":"pro","status":"active"}"""); // the webhook landed
        cut.WaitForAssertion(() => Assert.Equal("Plan_pro", cut.Find("[data-testid='billing-plan']").TextContent.Trim()), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Billing_CancelledSubscription_ReadsAsEnded_AndOffersNoPortal()
    {
        // After a cancellation the entitlement is free but the record is still "canceled" with its period end:
        // that date is when the paid plan ENDED (never "renews"), and there is nothing live to manage.
        await SignInAsync();
        StubFeatures(); // GATES-1: the page leaves immediately when billing is gated off
        Http.On(HttpMethod.Get, "/api/billing", """{"plan_key":"free","status":"canceled","current_period_end":"2026-10-07T00:00:00+00:00","has_subscription":true}""");

        var cut = Render<Billing>();
        cut.WaitForElement("[data-testid='billing-plan']");

        Assert.Contains("Billing_EndedOn[", cut.Find("[data-testid='billing-ended']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='billing-renews']"));
        Assert.Empty(cut.FindAll("[data-testid='billing-portal']"));
        Assert.NotNull(cut.Find("[data-testid='billing-upgrade']"));

        StubFeatures(); // GATES-1: the page leaves immediately when billing is gated off

        Http.On(HttpMethod.Get, "/api/billing", """{"plan_key":"pro","status":"active","current_period_end":"2026-10-07T00:00:00+00:00","has_subscription":true}""");
        var live = Render<Billing>();
        live.WaitForElement("[data-testid='billing-renews']");
        Assert.NotNull(live.Find("[data-testid='billing-portal']"));
    }

    [Fact]
    public async Task Billing_ReturnFromCheckout_Cancel_ShowsTheHonestBanner_AndChangesNothing()
    {
        await SignInAsync();
        StubFeatures(); // GATES-1: the page leaves immediately when billing is gated off
        Http.On(HttpMethod.Get, "/api/billing", """{"plan_key":"free","status":"active"}""");
        Services.GetRequiredService<NavigationManager>().NavigateTo("/billing/cancel");

        var cut = Render<Billing>();
        cut.WaitForElement("[data-testid='billing-plan']");

        Assert.Contains("Billing_CheckoutCancelled", cut.Find("[data-testid='billing-checkout-cancel']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='billing-checkout-success']"));
        Assert.Equal("Plan_free", cut.Find("[data-testid='billing-plan']").TextContent.Trim());
    }
}
