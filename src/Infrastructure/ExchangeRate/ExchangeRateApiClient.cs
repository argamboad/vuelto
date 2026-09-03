using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vuelto.Core.Budget;

namespace Vuelto.Infrastructure.ExchangeRate;

/// <summary>
/// exchangerate-api.com client (free tier, 1,500 req/month) behind <see cref="IExchangeRateService"/>.
/// A rate fetched within the freshness window counts as live — at household scale transactions don't
/// happen every minute, so the cache bounds quota use. The cache entry never expires on its own: when a
/// refresh fails the stale value is served flagged not-live with its original timestamp (ADR-V006).
/// <para>
/// R76 note: the destination is the configured vendor host plus the API key and two validated ISO
/// codes — nothing tenant-supplied reaches the URL, hence the allowlist entry in the arch test rather
/// than the outbound-URL guard.
/// </para>
/// </summary>
public sealed partial class ExchangeRateApiClient(
    HttpClient httpClient,
    IMemoryCache cache,
    IOptions<ExchangeRateSettings> options,
    TimeProvider clock,
    ILogger<ExchangeRateApiClient> logger) : IExchangeRateService
{
    private sealed record CachedRate(decimal Rate, DateTimeOffset FetchedAt);

    public async Task<ExchangeRateQuote> GetQuoteAsync(string fromCurrency, string toCurrency, CancellationToken cancellationToken = default)
    {
        var from = Code(fromCurrency);
        var to = Code(toCurrency);
        var settings = options.Value;
        var cacheKey = $"exchange-rate:{from}:{to}";
        var now = clock.GetUtcNow();

        cache.TryGetValue(cacheKey, out CachedRate? cached);
        if (cached is not null && now - cached.FetchedAt < TimeSpan.FromMinutes(settings.FreshnessMinutes))
            return new ExchangeRateQuote(cached.Rate, cached.FetchedAt, IsLive: true);

        try
        {
            var rate = await FetchAsync(settings, from, to, cancellationToken);
            cache.Set(cacheKey, new CachedRate(rate, now));
            return new ExchangeRateQuote(rate, now, IsLive: true);
        }
        catch (ExchangeRateUnavailableException)
        {
            if (cached is null) throw;
            logger.LogWarning("Exchange rate refresh failed for {From}->{To}; serving the stale rate from {FetchedAt}", from, to, cached.FetchedAt);
            return new ExchangeRateQuote(cached.Rate, cached.FetchedAt, IsLive: false);
        }
    }

    private async Task<decimal> FetchAsync(ExchangeRateSettings settings, string from, string to, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new ExchangeRateUnavailableException("Exchange rate provider is not configured (ExchangeRate:ApiKey)");

        try
        {
            var url = $"{settings.BaseUrl.TrimEnd('/')}/{settings.ApiKey}/pair/{from}/{to}";
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new ExchangeRateUnavailableException($"Exchange rate provider returned {(int)response.StatusCode}");

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (!root.TryGetProperty("result", out var result) || result.GetString() != "success")
                throw new ExchangeRateUnavailableException("Exchange rate provider reported a failure");

            var rate = root.GetProperty("conversion_rate").GetDecimal();
            if (rate <= 0) // donor US-034: a non-positive rate is unavailable, never stored
                throw new ExchangeRateUnavailableException($"Exchange rate provider returned a non-positive rate ({rate})");

            logger.LogInformation("Fetched exchange rate {From}->{To}: {Rate}", from, to, rate);
            return rate;
        }
        catch (ExchangeRateUnavailableException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Exchange rate fetch failed for {From}->{To}", from, to);
            throw new ExchangeRateUnavailableException("Exchange rate fetch failed", ex);
        }
    }

    /// <summary>Only an upper-case ISO-4217 code may enter the URL (R76).</summary>
    private static string Code(string currency)
    {
        var code = currency?.Trim().ToUpperInvariant() ?? "";
        return IsoCode().IsMatch(code) ? code : throw new ArgumentException($"'{currency}' is not an ISO currency code", nameof(currency));
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex IsoCode();
}
