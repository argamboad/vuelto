namespace Perezosoft.Infrastructure.Billing;

/// <summary>
/// Stripe configuration (bound from the <c>Billing:Stripe</c> section). Secrets come from <c>.env</c>
/// in dev / environment variables in prod (ADR-001) — never appsettings.
/// </summary>
public sealed class StripeSettings
{
    /// <summary>Stripe secret API key. When unset, the app falls back to <c>FakeBillingProvider</c>.</summary>
    public string? SecretKey { get; init; }

    /// <summary>Override the Stripe API base URL — used to point tests at <c>stripe-mock</c>. Null = real Stripe.</summary>
    public string? ApiBase { get; init; }

    /// <summary>Plan key → Stripe price id (<c>Billing:Stripe:Prices:{planKey}</c>).</summary>
    public Dictionary<string, string> Prices { get; init; } = new();

    /// <summary>Stripe webhook signing secret (<c>whsec_…</c>) used to verify inbound webhooks (BILLING-3).</summary>
    public string? WebhookSecret { get; init; }

    /// <summary>Reverse of <see cref="Prices"/>: Stripe price id → plan key, or null if unmapped.</summary>
    public string? PlanForPrice(string priceId) =>
        Prices.FirstOrDefault(kv => kv.Value == priceId).Key;
}
