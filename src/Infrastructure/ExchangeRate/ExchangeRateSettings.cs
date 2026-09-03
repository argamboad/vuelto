namespace Vuelto.Infrastructure.ExchangeRate;

/// <summary>
/// exchangerate-api.com configuration (bound from the <c>ExchangeRate</c> section). The key comes from
/// <c>.env</c> in dev / environment variables in prod (ADR-001) — never appsettings. With no key the
/// provider reports itself unavailable without making a call, and the resolver falls down its chain.
/// </summary>
public sealed class ExchangeRateSettings
{
    /// <summary>Provider API key. Unset ⇒ no live rates (fallback chain only).</summary>
    public string? ApiKey { get; init; }

    /// <summary>Provider base URL — a fixed vendor host; tests point it at a stub.</summary>
    public string BaseUrl { get; init; } = "https://v6.exchangerate-api.com/v6";

    /// <summary>How long a fetched rate counts as live before it is refreshed (quota protection).</summary>
    public int FreshnessMinutes { get; init; } = 60;
}
