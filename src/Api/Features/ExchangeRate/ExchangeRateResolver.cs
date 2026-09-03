using Vuelto.Core.Budget;

namespace Vuelto.Api.Features.ExchangeRate;

/// <summary>
/// The ADR-V006 chain: live quote → stale provider cache (both via <see cref="IExchangeRateService"/>)
/// → the household's most recent transaction rate (<see cref="IRecentRateSource"/>) → null. The pair is
/// fixed at USD→CRC (ADR-V004: every amount lives in both).
/// </summary>
public sealed class ExchangeRateResolver(
    IExchangeRateService provider,
    IRecentRateSource recent,
    ILogger<ExchangeRateResolver> logger) : IExchangeRateResolver
{
    public async Task<ResolvedRate?> ResolveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var quote = await provider.GetQuoteAsync(Currencies.Usd, Currencies.Crc, cancellationToken);
            return new ResolvedRate(quote.Rate, quote.IsLive ? RateSources.Live : RateSources.Cache, quote.AsOf);
        }
        catch (ExchangeRateUnavailableException ex)
        {
            logger.LogWarning(ex, "No live or cached exchange rate; falling back to the household's most recent transaction");
        }

        if (await recent.GetMostRecentAsync(cancellationToken) is { } last)
            return new ResolvedRate(last.Rate, RateSources.Transaction, last.AsOf);

        logger.LogWarning("Exchange rate unresolvable: no provider rate and no transaction to fall back to");
        return null;
    }
}

/// <summary>P3's last tier: no transactions exist yet, so there is never a recent rate. P5 replaces the registration.</summary>
public sealed class NoRecentRateSource : IRecentRateSource
{
    public Task<RecentRate?> GetMostRecentAsync(CancellationToken cancellationToken = default) => Task.FromResult<RecentRate?>(null);
}
