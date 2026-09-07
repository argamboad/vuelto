namespace Vuelto.Infrastructure.ExchangeRate;

/// <summary>
/// Exchange-rate provider configuration (bound from the <c>ExchangeRate</c> section). The key comes from
/// <c>.env</c> in dev / environment variables in prod (ADR-001) — never appsettings. With no key the
/// provider reports itself unavailable without making a call, and the resolver falls down its chain.
/// </summary>
public sealed class ExchangeRateSettings
{
    /// <summary>Which provider serves the day's quote (ADR-V019): <c>bccr</c> (default — the Banco Central reference buy/sell pair via the Finance Ministry mirror, no key) or <c>exchangerate-api</c> (the world feed, one mid rate, needs <see cref="ApiKey"/>).</summary>
    public string Provider { get; init; } = "bccr";

    /// <summary>The BCCR reference-rate mirror (JSON: dolar.compra / dolar.venta with a publication date).</summary>
    public string BccrUrl { get; init; } = "https://api.hacienda.go.cr/indicadores/tc";

    /// <summary>exchangerate-api.com key. Only read when <see cref="Provider"/> is <c>exchangerate-api</c>; unset ⇒ that provider reports unavailable (fallback chain only).</summary>
    public string? ApiKey { get; init; }

    /// <summary>Provider base URL — a fixed vendor host; tests point it at a stub.</summary>
    public string BaseUrl { get; init; } = "https://v6.exchangerate-api.com/v6";

    /// <summary>How long a fetched rate counts as live before it is refreshed (quota protection).</summary>
    public int FreshnessMinutes { get; init; } = 60;
}
