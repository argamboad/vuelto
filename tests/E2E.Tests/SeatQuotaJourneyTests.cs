using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Vuelto.E2E.Tests.Pages;

namespace Vuelto.E2E.Tests;

/// <summary>
/// Billing seat-quota journey (E2E-2): the free plan's seat limit is reachable entirely from the
/// browser — seats = members + pending invitations (QuotaService, BILLING-5) — so a fresh household
/// hits it with pending invites and sees the 402 upgrade prompt, no Stripe involved. The money path
/// (Checkout/webhooks) stays covered by tests/Api.Tests/Billing. Maps to QA-HH-14 and QA-INV-08.
/// </summary>
[TestFixture]
public class SeatQuotaJourneyTests : E2ETestBase
{
    // Free-plan SeatLimit from src/Core/Billing/PlanCatalog.cs ("EXAMPLE quotas — tune per app").
    // A fresh household starts with 1 seat used (the owner); if a downstream app retunes the
    // catalog, adjust this and the invite count follows.
    private const int FreePlanSeatLimit = 3;

    private static readonly LocatorAssertionsToBeVisibleOptions Slow = new() { Timeout = 30_000 };

    [Test]
    public async Task Inviting_Past_The_SeatLimit_Shows_The_Upgrade_Prompt()
    {
        var household = await SignInToHouseholdAsync(Page, UniqueEmail("owner"));

        // Fill the remaining seats with pending invitations (a pending invite reserves a seat).
        var pendingEmails = await FillRemainingSeatsAsync(household);

        // One more is over the limit: the 402 upgrade prompt shows and no new row appears.
        var overLimitEmail = UniqueEmail("overlimit");
        await household.InviteEmail.FillAsync(overLimitEmail);
        await household.InviteEmail.BlurAsync();
        await household.InviteSend.ClickAsync();

        await Expect(household.Status).ToBeVisibleAsync(new() { Timeout = 15_000 });
        await Expect(household.Status).ToContainTextAsync("Upgrade"); // Household_ErrSeatLimit (EN)
        await Expect(household.PendingRow(overLimitEmail)).Not.ToBeVisibleAsync();
        await Expect(household.PendingRows).ToHaveCountAsync(pendingEmails.Count);
    }

    [Test]
    public async Task Revoking_A_Pending_Invitation_Frees_The_Seat()
    {
        var household = await SignInToHouseholdAsync(Page, UniqueEmail("owner"));
        var pendingEmails = await FillRemainingSeatsAsync(household);

        await household.Revoke(pendingEmails[0]).ClickAsync();
        await Expect(household.PendingRow(pendingEmails[0]))
            .Not.ToBeVisibleAsync(new() { Timeout = 15_000 });

        // The freed seat makes a new invitation succeed.
        var replacementEmail = UniqueEmail("replacement");
        var token = await household.InviteAsync(replacementEmail);
        Assert.That(token, Is.Not.Empty);
        await Expect(household.PendingRow(replacementEmail)).ToBeVisibleAsync(Slow);
    }

    /// <summary>
    /// BILLING-9: the seat quota is re-checked at ACCEPT. Upgrade to Pro via the fake provider,
    /// invite past the Free cap (legal on Pro), downgrade via a "canceled" webhook (the dunning/
    /// comp-revert equivalent), then redeem a still-valid token — the join page shows the
    /// household-full state instead of joining. Maps to QA-INV-10.
    /// </summary>
    [Test]
    public async Task Accepting_An_Invite_After_A_Downgrade_Shows_The_HouseholdFull_State()
    {
        await Mailpit.ClearAsync();
        var household = await SignInToHouseholdAsync(Page, UniqueEmail("owner"));

        // Upgrade to Pro exactly like the billing journey: stubbed checkout redirect carries the
        // tenant id; the webhook flips the projection.
        var billing = new BillingPage(Page);
        await billing.GotoAsync();
        await Page.RouteAsync("https://billing.test/**", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "text/html",
            Body = "<html><body>Fake checkout</body></html>",
        }));
        await billing.Upgrade.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(@"billing\.test/checkout/.+/pro"), new() { Timeout = 30_000 });
        var tenantId = Regex.Match(Page.Url, @"checkout/([0-9a-fA-F-]+)/pro").Groups[1].Value;
        await PostBillingWebhookAsync(tenantId, status: "active", occurredAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        // On Pro (10 seats), reserve more seats than Free allows: owner + 3 pending = 4 > 3.
        await household.GotoAsync();
        var lateToken = "";
        for (var i = 1; i <= FreePlanSeatLimit; i++)
            lateToken = await household.InviteAsync(UniqueEmail($"pro-invitee{i}"));

        // The provider cancels (strictly-newer event) → the plan resolves Free again.
        await PostBillingWebhookAsync(tenantId, status: "canceled");

        // A fresh user redeems the still-valid token: refused with the household-full state —
        // nothing joined, and the token stays pending (self-heals if the owner re-upgrades).
        await Mailpit.ClearAsync();
        await using var inviteeCtx = await Browser.NewContextAsync(ContextOptions());
        var inviteePage = await inviteeCtx.NewPageAsync();
        await SignInAsync(inviteePage, UniqueEmail("late-joiner"));

        var join = new JoinPage(inviteePage);
        await join.GotoWithTokenAsync(lateToken);
        await Expect(join.HouseholdFull).ToBeVisibleAsync(Slow);
        await Expect(join.Success).Not.ToBeVisibleAsync();
    }

    /// <summary>Invites until members + pending == the seat limit; returns the invited emails.</summary>
    private static async Task<List<string>> FillRemainingSeatsAsync(HouseholdPage household)
    {
        var invited = new List<string>();
        for (var i = 1; i <= FreePlanSeatLimit - 1; i++)
        {
            var email = UniqueEmail($"invitee{i}");
            await household.InviteAsync(email);
            await Assertions.Expect(household.PendingRow(email))
                .ToBeVisibleAsync(new() { Timeout = 15_000 });
            invited.Add(email);
        }
        return invited;
    }
}
