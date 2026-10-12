namespace Vuelto.Infrastructure.Billing;

/// <summary>
/// Stripe configuration (bound from the <c>Billing:Stripe</c> section). Secrets come from <c>.env</c>
/// in dev / environment variables in prod (ADR-001) — never appsettings.
/// </summary>
public sealed class StripeSettings
{
    public const string SectionName = "Billing:Stripe";

    /// <summary>Stripe secret API key. When unset, the app falls back to <c>FakeBillingProvider</c>.</summary>
    public string? SecretKey { get; init; }

    /// <summary>Override the Stripe API base URL — used to point tests at <c>stripe-mock</c>. Null = real Stripe.</summary>
    public string? ApiBase { get; init; }

    /// <summary>Plan key → Stripe price id (<c>Billing:Stripe:Prices:{planKey}</c>).</summary>
    public Dictionary<string, string> Prices { get; init; } = new();

    /// <summary>Stripe webhook signing secret (<c>whsec_…</c>) used to verify inbound webhooks (BILLING-3).</summary>
    public string? WebhookSecret { get; init; }

    /// <summary>
    /// The mode this deployment expects its Stripe traffic in (<c>true</c> = live). Unset ⇒ inferred from the
    /// secret key's prefix. A webhook whose <c>livemode</c> disagrees is ignored (v4 T46): a test-mode signing
    /// secret left in production would otherwise let Dashboard test events change real tenants' plans.
    /// </summary>
    public bool? ExpectLiveKey { get; init; }

    /// <summary>Whether events are expected live; null when nothing says (no key, no expectation).</summary>
    public bool? ExpectsLiveEvents => ExpectLiveKey ?? (SecretKey is { } key ? key.StartsWith("sk_live_", StringComparison.Ordinal) : null);

    /// <summary>Reverse of <see cref="Prices"/>: Stripe price id → plan key, or null if unmapped.</summary>
    public string? PlanForPrice(string priceId) =>
        Prices.FirstOrDefault(kv => kv.Value == priceId).Key;
}
