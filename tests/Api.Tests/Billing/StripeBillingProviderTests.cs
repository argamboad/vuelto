using Microsoft.Extensions.Options;
using Vuelto.Core.Entities;
using Vuelto.Core.Billing;
using Stripe;
using Microsoft.Extensions.Logging;
using Vuelto.Core.Abstractions;
using Vuelto.Infrastructure.Billing;

namespace Vuelto.Api.Tests.Billing;

/// <summary>
/// Unit coverage for the deterministic part of <see cref="StripeBillingProvider"/> — the
/// price-resolution guard — which needs no Stripe or network. The live SDK wiring (an actual Checkout
/// session round-trip) is exercised against <b>stripe-mock</b> / Stripe test mode at the E2E layer per
/// the billing story DoD; <see cref="BillingHandlerTests"/> covers all BILLING-2 acceptance criteria
/// offline via <c>FakeBillingProvider</c>.
/// </summary>
public class StripeBillingProviderTests
{
    // --- the webhook mapping fails safe and loud (v4 T46: LB-BILL-21/22/27) — real signed fixtures ---

    private const string Secret = "whsec_test_fixture";
    internal const string FixtureSecret = Secret;
    internal static (string Payload, string Signature) SignedSubscriptionEventFor(Guid tenant, string priceId) =>
        SignedSubscriptionEvent(tenant, priceId);

    /// <summary>A signed <c>customer.subscription.updated</c> the way Stripe would send it; knobs for the three gaps.</summary>
    private static (string Payload, string Signature) SignedSubscriptionEvent(Guid tenant, string priceId = "price_pro",
        long? currentPeriodEnd = 1_800_000_000, bool livemode = false)
    {
        var period = currentPeriodEnd is { } end ? $",\"current_period_end\":{end}" : "";
        var mode = livemode ? "true" : "false";
        var payload = "{\"id\":\"evt_fixture\",\"object\":\"event\",\"api_version\":\"" + StripeConfiguration.ApiVersion + "\",\"created\":1790000000,\"livemode\":" + mode
            + ",\"type\":\"customer.subscription.updated\",\"data\":{\"object\":{\"id\":\"sub_fixture\",\"object\":\"subscription\",\"customer\":\"cus_fixture\""
            + ",\"status\":\"active\",\"livemode\":" + mode + ",\"metadata\":{\"tenant_id\":\"" + tenant + "\"}"
            + ",\"items\":{\"object\":\"list\",\"data\":[{\"id\":\"si_fixture\",\"object\":\"subscription_item\",\"price\":{\"id\":\"" + priceId + "\",\"object\":\"price\"}" + period + "}]}}}}";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = EventUtility.ComputeSignature(Secret, timestamp, payload);
        return (payload, $"t={timestamp},v1={signature}");
    }

    [Fact]
    public void SignedFixture_MapsThePlan_ThePeriodEnd_AndTheIds()
    {
        var tenant = Guid.CreateVersion7();
        var (payload, signature) = SignedSubscriptionEvent(tenant);

        var evt = new StripeBillingProvider(Options.Create(new StripeSettings
        {
            WebhookSecret = Secret, Prices = new Dictionary<string, string> { [PlanKeys.Pro] = "price_pro" },
        })).ParseWebhookEvent(payload, signature);

        Assert.NotNull(evt);
        Assert.Equal(tenant, evt!.TenantId);
        Assert.Equal(PlanKeys.Pro, evt.PlanKey);
        Assert.Equal(SubscriptionStatus.Active, evt.Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), evt.CurrentPeriodEnd);
        Assert.Equal("sub_fixture", evt.StripeSubscriptionId);
    }

    [Fact]
    public void MissingCurrentPeriodEnd_BecomesNull_WithAWarning_NotYearOne()
    {
        // Stripe.net's SubscriptionItem.CurrentPeriodEnd is a non-nullable DateTime: absent, it arrived as a
        // sentinel (the Unix epoch here; 0001-01-01 in the audit's reading), the projection stored it, the
        // entitlement read it as a lapsed period (a paying tenant resolved to Free), the activation email said
        // "renews on 1970-01-01" and the sweep nudged them as lapsed every six hours.
        var log = new CapturingLogger<StripeBillingProvider>();
        var provider = new StripeBillingProvider(Options.Create(new StripeSettings
        {
            WebhookSecret = Secret, Prices = new Dictionary<string, string> { [PlanKeys.Pro] = "price_pro" },
        }), log);
        var (payload, signature) = SignedSubscriptionEvent(Guid.CreateVersion7(), currentPeriodEnd: null);

        var evt = provider.ParseWebhookEvent(payload, signature);

        Assert.NotNull(evt);
        Assert.Null(evt!.CurrentPeriodEnd);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("current_period_end"));
    }

    [Fact]
    public void UnmappedPrice_IsAcknowledgedNotApplied_AndLoggedAsAnError()
    {
        // A price missing from Billing:Stripe:Prices became an ACTIVE FREE projection with no log: the paying
        // tenant sat on Free with a "Your Free plan is active" email, and staff could not comp around a
        // provider-managed row. Null ⇒ the handler acknowledges (200) and writes nothing.
        var log = new CapturingLogger<StripeBillingProvider>();
        var provider = new StripeBillingProvider(Options.Create(new StripeSettings
        {
            WebhookSecret = Secret, Prices = new Dictionary<string, string> { [PlanKeys.Pro] = "price_pro" },
        }), log);
        var (payload, signature) = SignedSubscriptionEvent(Guid.CreateVersion7(), priceId: "price_not_configured");

        Assert.Null(provider.ParseWebhookEvent(payload, signature));
        var error = Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("price_not_configured", error.Message);
        Assert.Contains("Billing:Stripe:Prices", error.Message);
    }

    [Theory]
    [InlineData(true, false)]  // production expects live, a Dashboard TEST event arrives
    [InlineData(false, true)]  // a test deployment gets a live event
    public void LivemodeMismatch_IsIgnored_WithAWarning(bool expectLive, bool eventLivemode)
    {
        var log = new CapturingLogger<StripeBillingProvider>();
        var provider = new StripeBillingProvider(Options.Create(new StripeSettings
        {
            WebhookSecret = Secret, ExpectLiveKey = expectLive, Prices = new Dictionary<string, string> { [PlanKeys.Pro] = "price_pro" },
        }), log);
        var (payload, signature) = SignedSubscriptionEvent(Guid.CreateVersion7(), livemode: eventLivemode);

        Assert.Null(provider.ParseWebhookEvent(payload, signature));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("expects"));
    }

    [Fact]
    public void ExpectedMode_IsInferredFromTheSecretKey_WhenNotConfigured()
    {
        // sk_live_ ⇒ live events expected; a test event under a live key is the misconfiguration the check exists for.
        var log = new CapturingLogger<StripeBillingProvider>();
        var provider = new StripeBillingProvider(Options.Create(new StripeSettings
        {
            SecretKey = "sk_live_fixture", WebhookSecret = Secret, Prices = new Dictionary<string, string> { [PlanKeys.Pro] = "price_pro" },
        }), log);
        var (payload, signature) = SignedSubscriptionEvent(Guid.CreateVersion7(), livemode: false);

        Assert.Null(provider.ParseWebhookEvent(payload, signature));
        Assert.Null(new StripeSettings().ExpectsLiveEvents); // nothing configured, nothing inferred: no check
    }

    [Fact]
    public async Task CreateCheckoutSession_WithNoPriceConfiguredForPlan_Throws()
    {
        // SecretKey set but no price mapping → misconfiguration; must fail fast before any Stripe call.
        var provider = new StripeBillingProvider(Options.Create(new StripeSettings
        {
            SecretKey = "sk_test_123",
            Prices = new Dictionary<string, string>(), // no "pro" price
        }));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.CreateCheckoutSessionAsync(
                new BillingCheckoutRequest(Guid.CreateVersion7(), "pro", "https://app/success", "https://app/cancel")));

        Assert.Contains("pro", ex.Message);
    }
}
