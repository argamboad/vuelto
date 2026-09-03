using Microsoft.Extensions.Logging.Abstractions;
using Vuelto.Api.Features.ExchangeRate;
using Vuelto.Core.Budget;

namespace Vuelto.Api.Tests.Features;

/// <summary>
/// FX-1 / ADR-V006 chain: live quote, then stale cache (both via the provider seam), then the household's
/// most recent transaction rate (the <see cref="IRecentRateSource"/> seam P5 fills), then null so the
/// caller blocks. Also pins the pair to USD→CRC.
/// </summary>
public class ExchangeRateResolverTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private sealed class Provider(Func<(string From, string To), ExchangeRateQuote> quote) : IExchangeRateService
    {
        public (string From, string To)? Asked { get; private set; }
        public Task<ExchangeRateQuote> GetQuoteAsync(string fromCurrency, string toCurrency, CancellationToken cancellationToken = default)
        {
            Asked = (fromCurrency, toCurrency);
            return Task.FromResult(quote((fromCurrency, toCurrency)));
        }
    }

    private sealed class Recent(RecentRate? rate) : IRecentRateSource
    {
        public Task<RecentRate?> GetMostRecentAsync(CancellationToken cancellationToken = default) => Task.FromResult(rate);
    }

    private static Provider Down() => new(_ => throw new ExchangeRateUnavailableException("down"));

    private static ExchangeRateResolver Resolver(IExchangeRateService provider, IRecentRateSource? recent = null) =>
        new(provider, recent ?? new NoRecentRateSource(), NullLogger<ExchangeRateResolver>.Instance);

    [Fact]
    public async Task LiveQuote_IsSourceLive_ForUsdToCrc()
    {
        var provider = new Provider(_ => new ExchangeRateQuote(510.45m, T0, IsLive: true));

        var resolved = await Resolver(provider).ResolveAsync();

        Assert.Equal(new ResolvedRate(510.45m, RateSources.Live, T0), resolved);
        Assert.Equal((Currencies.Usd, Currencies.Crc), provider.Asked);
    }

    [Fact]
    public async Task StaleQuote_IsSourceCache_WithTheFetchTime()
    {
        var fetched = T0.AddHours(-5);
        var provider = new Provider(_ => new ExchangeRateQuote(508m, fetched, IsLive: false));

        var resolved = await Resolver(provider).ResolveAsync();

        Assert.Equal(new ResolvedRate(508m, RateSources.Cache, fetched), resolved);
    }

    [Fact]
    public async Task ProviderDown_FallsBackToTheMostRecentTransactionRate()
    {
        var frozen = T0.AddDays(-2);

        var resolved = await Resolver(Down(), new Recent(new RecentRate(505.20m, frozen))).ResolveAsync();

        Assert.Equal(new ResolvedRate(505.20m, RateSources.Transaction, frozen), resolved);
    }

    [Fact]
    public async Task ProviderDown_AndNothingRecent_IsNull()
    {
        Assert.Null(await Resolver(Down()).ResolveAsync());                      // P3's NoRecentRateSource
        Assert.Null(await Resolver(Down(), new Recent(null)).ResolveAsync());    // a source with no rows
    }
}
