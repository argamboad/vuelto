using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Vuelto.E2E.Tests.Pages;

namespace Vuelto.E2E.Tests;

/// <summary>
/// Billing page journey (BILLING-8): the full upgrade loop with NO Stripe involvement, using the
/// FakeBillingProvider (active because the E2E API runs without Billing:Stripe:SecretKey — README).
/// The checkout redirect goes to the fake's deterministic https://billing.test URL (stubbed via
/// Playwright routing; the tenant id is parsed from it), and "payment completed" is simulated by
/// POSTing the provider webhook exactly as Stripe would — exercising the real signature check,
/// inbox dedup, EnterTenant, and subscription projection. Maps to QA-BILL-01/02.
/// </summary>
[TestFixture]
public class BillingJourneyTests : E2ETestBase
{
    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    // Seat limits mirror src/Core/Billing/PlanCatalog.cs ("EXAMPLE quotas — tune per app"); keep them
    // in one place here so a downstream re-tune is a one-line change, as SeatQuotaJourneyTests does.
    private const int FreePlanSeatLimit = 5; // GATES-1 (ADR-027) raised Free 3 → 5
    private const int ProPlanSeatLimit = 10;

    [Test]
    public async Task Owner_Upgrades_Via_Checkout_And_Webhook_Lands_On_Pro()
    {
        var household = await SignInToHouseholdAsync(Page, UniqueEmail("billing-owner"));
        await Expect(household.MemberRows).ToHaveCountAsync(1);

        // Fresh tenant: Free plan, one seat used (the owner), never subscribed → no portal button.
        var billing = new BillingPage(Page);
        await billing.GotoAsync();
        // UX-5: the page renders the LOCALIZED plan/status labels (EN culture here), not the raw API tokens.
        await Expect(billing.Plan).ToHaveTextAsync("Free", new() { Timeout = 30_000 });
        // Assert the used count and let the LIMIT follow the catalog, rather than pinning a number
        // that a re-tune silently invalidates (this is exactly what GATES-1's 3 → 5 broke).
        await Expect(billing.Seats).ToContainTextAsync($"1 of {FreePlanSeatLimit}");
        await Expect(billing.Portal).Not.ToBeVisibleAsync();

        // The hosted-checkout domain is external and fake — stub it so the redirect can land.
        await Page.RouteAsync("https://billing.test/**", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "text/html",
            Body = "<html><body>Fake checkout</body></html>",
        }));

        await billing.Upgrade.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(@"billing\.test/checkout/.+/pro"), new() { Timeout = 30_000 });

        // The fake's checkout URL is /checkout/{tenantId}/{planKey} — the tenant id keys the webhook.
        var tenantId = Regex.Match(Page.Url, @"checkout/([0-9a-fA-F-]+)/pro").Groups[1].Value;
        Assert.That(tenantId, Is.Not.Empty);

        // Simulate the provider's "subscription active" callback — the same POST Stripe would make.
        await PostBillingWebhookAsync(tenantId, status: "active");

        // Back on the billing page: the projection made the tenant Pro (more seats, portal available).
        await billing.GotoAsync();
        await Expect(billing.Plan).ToHaveTextAsync("Pro", new() { Timeout = 30_000 });
        await Expect(billing.Status).ToHaveTextAsync("Active");
        await Expect(billing.Seats).ToContainTextAsync($"1 of {ProPlanSeatLimit}");
        await Expect(billing.Portal).ToBeVisibleAsync();
    }

    [Test]
    public async Task Member_Sees_The_OwnerOnly_State()
    {
        var (ownerEmail, memberEmail) = (UniqueEmail("owner"), UniqueEmail("member"));
        var household = await SignInToHouseholdAsync(Page, ownerEmail);

        await using var memberCtx = await Browser.NewContextAsync(ContextOptions());
        var memberPage = await memberCtx.NewPageAsync();
        await InviteAndJoinAsync(household, memberPage, memberEmail);

        var billing = new BillingPage(memberPage);
        await billing.GotoAsync();
        await Expect(billing.OwnerOnly).ToBeVisibleAsync(Slow);
        await Expect(billing.Plan).Not.ToBeVisibleAsync();
        await Expect(billing.Upgrade).Not.ToBeVisibleAsync();
    }
}
