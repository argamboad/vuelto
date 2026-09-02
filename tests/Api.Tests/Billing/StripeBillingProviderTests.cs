using Microsoft.Extensions.Options;
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
