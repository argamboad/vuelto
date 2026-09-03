namespace Vuelto.Core.Budget;

// FX-1 (port slice P3, ADR-V006): the exchange-rate seams. Core owns the contracts so any slice can
// ask for a rate (P5's transaction path freezes it) without referencing another slice's namespace (R7).

/// <summary>
/// A conversion quote from the provider (or its cache). <see cref="IsLive"/> is false when the value is
/// a stale cached rate served because the provider was down; <see cref="AsOf"/> is when it was fetched.
/// </summary>
public record ExchangeRateQuote(decimal Rate, DateTimeOffset AsOf, bool IsLive);

/// <summary>
/// The live-rate provider (implemented in Infrastructure so the vendor is swappable). A rate cached
/// within the freshness window counts as live (free-tier quota); when the provider fails, a stale
/// cached rate comes back flagged not-live.
/// </summary>
public interface IExchangeRateService
{
    /// <exception cref="ExchangeRateUnavailableException">
    /// The provider failed (or is not configured) and nothing is cached — callers fall down the chain.
    /// </exception>
    Task<ExchangeRateQuote> GetQuoteAsync(string fromCurrency, string toCurrency, CancellationToken cancellationToken = default);
}

/// <summary>The provider could not supply a rate (down, misconfigured, or a non-positive value).</summary>
public sealed class ExchangeRateUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Where a resolved rate came from — the wire values of <c>source</c>.</summary>
public static class RateSources
{
    public const string Live = "live";
    public const string Cache = "cache";
    public const string Transaction = "transaction";
}

/// <summary>A USD→CRC rate resolved through the ADR-V006 chain, with its provenance.</summary>
public record ResolvedRate(decimal Rate, string Source, DateTimeOffset AsOf);

/// <summary>
/// The ADR-V006 fallback chain for the household on the current token: live quote → stale cache →
/// the household's most recent transaction rate. Null means nothing is available and the caller must
/// block with a clear message (<c>exchange_rate_unavailable</c>).
/// </summary>
public interface IExchangeRateResolver
{
    Task<ResolvedRate?> ResolveAsync(CancellationToken cancellationToken = default);
}

/// <summary>The rate a household used most recently, with when it was frozen.</summary>
public record RecentRate(decimal Rate, DateTimeOffset AsOf);

/// <summary>
/// The chain's last tier. Tenant-ambient like every repository read. P3 registers a source that has
/// nothing (no transactions exist yet); P5 replaces it with one that reads the household's latest
/// transaction — the resolver never changes.
/// </summary>
public interface IRecentRateSource
{
    Task<RecentRate?> GetMostRecentAsync(CancellationToken cancellationToken = default);
}
