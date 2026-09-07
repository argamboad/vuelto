using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vuelto.Core.Budget;

namespace Vuelto.Infrastructure.ExchangeRate;

/// <summary>
/// Banco Central de Costa Rica reference rates (ADR-V019) behind <see cref="IExchangeRateService"/>, read
/// from the Ministry of Finance's public mirror (<c>api.hacienda.go.cr/indicadores/tc</c>: no key, no
/// quota) — <c>dolar.compra</c> is the <see cref="FxRates.Buy"/> side, <c>dolar.venta</c> the
/// <see cref="FxRates.Sell"/> side, both stamped with the publication date. The reference rate changes once
/// a day and is not published on weekends/holidays, so the last published pair is simply the current one.
/// Same cache contract as the world-feed client: fresh within the freshness window ⇒ live, a failed
/// refresh serves the stale pair flagged not-live with its original fetch time, failures are never cached.
/// <para>R76 note: the URL is the configured fixed host and nothing else — nothing tenant-supplied reaches it.</para>
/// </summary>
public sealed class BccrExchangeRateClient(
    HttpClient httpClient,
    IMemoryCache cache,
    IOptions<ExchangeRateSettings> options,
    TimeProvider clock,
    ILogger<BccrExchangeRateClient> logger) : IExchangeRateService
{
    private const string CacheKey = "exchange-rate:bccr:USD:CRC";

    private sealed record CachedRates(FxRates Rates, DateTimeOffset FetchedAt);

    public async Task<ExchangeRateQuote> GetQuoteAsync(string fromCurrency, string toCurrency, CancellationToken cancellationToken = default)
    {
        if (!Is(fromCurrency, Currencies.Usd) || !Is(toCurrency, Currencies.Crc))
            throw new ExchangeRateUnavailableException($"BCCR publishes USD→CRC only (asked for {fromCurrency}→{toCurrency})");

        var settings = options.Value;
        var now = clock.GetUtcNow();
        cache.TryGetValue(CacheKey, out CachedRates? cached);
        if (cached is not null && now - cached.FetchedAt < TimeSpan.FromMinutes(settings.FreshnessMinutes))
            return new ExchangeRateQuote(cached.Rates, cached.FetchedAt, IsLive: true);

        try
        {
            var rates = await FetchAsync(settings, cancellationToken);
            cache.Set(CacheKey, new CachedRates(rates, now));
            return new ExchangeRateQuote(rates, now, IsLive: true);
        }
        catch (ExchangeRateUnavailableException)
        {
            if (cached is null) throw;
            logger.LogWarning("BCCR rate refresh failed; serving the stale pair from {FetchedAt}", cached.FetchedAt);
            return new ExchangeRateQuote(cached.Rates, cached.FetchedAt, IsLive: false);
        }
    }

    private async Task<FxRates> FetchAsync(ExchangeRateSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.BccrUrl))
            throw new ExchangeRateUnavailableException("BCCR provider is not configured (ExchangeRate:BccrUrl)");

        try
        {
            using var response = await httpClient.GetAsync(settings.BccrUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new ExchangeRateUnavailableException($"BCCR provider returned {(int)response.StatusCode}");

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var dolar = document.RootElement.GetProperty("dolar");
            var buy = dolar.GetProperty("compra").GetProperty("valor").GetDecimal();
            var sell = dolar.GetProperty("venta").GetProperty("valor").GetDecimal();
            if (buy <= 0 || sell <= 0) // donor US-034: a non-positive rate is unavailable, never stored
                throw new ExchangeRateUnavailableException($"BCCR provider returned a non-positive rate (compra {buy}, venta {sell})");

            var published = dolar.GetProperty("venta").TryGetProperty("fecha", out var f) ? f.GetString() : null;
            logger.LogInformation("Fetched BCCR reference rates: compra {Buy}, venta {Sell} (published {Published})", buy, sell, published);
            return new FxRates(buy, sell);
        }
        catch (ExchangeRateUnavailableException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "BCCR rate fetch failed");
            throw new ExchangeRateUnavailableException("BCCR rate fetch failed", ex);
        }
    }

    private static bool Is(string a, string b) => string.Equals(a?.Trim(), b, StringComparison.OrdinalIgnoreCase);
}
