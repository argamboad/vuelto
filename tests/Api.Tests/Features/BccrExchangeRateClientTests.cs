using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Core.Budget;
using Vuelto.Infrastructure.ExchangeRate;

namespace Vuelto.Api.Tests.Features;

/// <summary>
/// ADR-V019 provider: the BCCR reference pair from the Finance Ministry mirror — compra = Buy, venta = Sell —
/// with the same cache contract as the world-feed client (fresh ⇒ live without a call, failed refresh ⇒ the
/// stale pair not-live with its original timestamp, failures never cached, non-positive ⇒ unavailable),
/// USD→CRC only, and a fixed URL with nothing appended (R76).
/// </summary>
public class BccrExchangeRateClientTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
        private (HttpStatusCode Status, string Body) _last = (HttpStatusCode.OK, Mirror(448.27m, 453.69m));
        public List<Uri> Requests { get; } = [];

        public StubHandler Then(HttpStatusCode status, string body) { _responses.Enqueue((status, body)); return this; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (_responses.Count > 0) _last = _responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(_last.Status) { Content = new StringContent(_last.Body) });
        }
    }

    /// <summary>The mirror's real shape (2026-09-07): dolar.compra / dolar.venta each with fecha + valor, plus euro.</summary>
    private static string Mirror(decimal compra, decimal venta, string fecha = "2026-09-07") =>
        $$$"""{"dolar":{"venta":{"fecha":"{{{fecha}}}","valor":{{{venta}}}},"compra":{"fecha":"{{{fecha}}}","valor":{{{compra}}}}},"euro":{"fecha":"{{{fecha}}}","dolares":1.1627,"colones":527.51}}""";

    private static (BccrExchangeRateClient Client, StubHandler Http, FakeTimeProvider Clock) Harness(string url = "https://mirror.test/indicadores/tc", int freshness = 60)
    {
        var http = new StubHandler();
        var clock = new FakeTimeProvider(T0);
        var client = new BccrExchangeRateClient(
            new HttpClient(http),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new ExchangeRateSettings { Provider = "bccr", BccrUrl = url, FreshnessMinutes = freshness }),
            clock,
            NullLogger<BccrExchangeRateClient>.Instance);
        return (client, http, clock);
    }

    [Fact]
    public async Task Quote_ParsesCompraAsBuy_VentaAsSell_IsLive_AndCallsTheFixedUrlOnly()
    {
        var (client, http, _) = Harness();

        var quote = await client.GetQuoteAsync("usd", "crc");

        Assert.Equal(new FxRates(448.27m, 453.69m), quote.Rates);
        Assert.Equal(453.69m, quote.Rate); // the "per $1" figure is the sell side
        Assert.True(quote.IsLive);
        Assert.Equal(T0, quote.AsOf);
        Assert.Equal("https://mirror.test/indicadores/tc", Assert.Single(http.Requests).ToString()); // nothing appended (R76)
    }

    [Fact]
    public async Task FreshCache_IsServedAsLive_WithoutACall_StaleCacheRefetches()
    {
        var (client, http, clock) = Harness();
        await client.GetQuoteAsync("USD", "CRC");

        clock.Advance(TimeSpan.FromMinutes(59));
        var cached = await client.GetQuoteAsync("USD", "CRC");
        Assert.Single(http.Requests);
        Assert.True(cached.IsLive);
        Assert.Equal(T0, cached.AsOf);

        http.Then(HttpStatusCode.OK, Mirror(449.00m, 454.50m, "2026-09-08"));
        clock.Advance(TimeSpan.FromMinutes(2));
        var refreshed = await client.GetQuoteAsync("USD", "CRC");
        Assert.Equal(2, http.Requests.Count);
        Assert.Equal(new FxRates(449.00m, 454.50m), refreshed.Rates);
    }

    [Fact]
    public async Task FailedRefresh_ServesTheStalePair_NotLive_WithItsOriginalTimestamp()
    {
        var (client, http, clock) = Harness();
        await client.GetQuoteAsync("USD", "CRC");

        http.Then(HttpStatusCode.ServiceUnavailable, "");
        clock.Advance(TimeSpan.FromHours(3));
        var stale = await client.GetQuoteAsync("USD", "CRC");

        Assert.Equal(new FxRates(448.27m, 453.69m), stale.Rates);
        Assert.False(stale.IsLive);
        Assert.Equal(T0, stale.AsOf);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, """{"euro":{"colones":527.51}}""")]
    public async Task ProviderFailure_WithNoCache_IsUnavailable(HttpStatusCode status, string body)
    {
        var (client, http, _) = Harness();
        http.Then(status, body);

        await Assert.ThrowsAsync<ExchangeRateUnavailableException>(() => client.GetQuoteAsync("USD", "CRC"));
    }

    [Theory]
    [InlineData(0, 453.69)]
    [InlineData(448.27, -1)]
    public async Task NonPositiveSide_IsUnavailable(double compra, double venta)
    {
        var (client, http, _) = Harness();
        http.Then(HttpStatusCode.OK, Mirror((decimal)compra, (decimal)venta));

        await Assert.ThrowsAsync<ExchangeRateUnavailableException>(() => client.GetQuoteAsync("USD", "CRC"));
    }

    [Fact]
    public async Task Failures_AreNotCached_AndRecoveryIsLiveAgain()
    {
        var (client, http, _) = Harness();
        http.Then(HttpStatusCode.BadGateway, "");
        await Assert.ThrowsAsync<ExchangeRateUnavailableException>(() => client.GetQuoteAsync("USD", "CRC"));

        http.Then(HttpStatusCode.OK, Mirror(448.27m, 453.69m));
        var recovered = await client.GetQuoteAsync("USD", "CRC");

        Assert.True(recovered.IsLive);
        Assert.Equal(2, http.Requests.Count);
    }

    [Theory]
    [InlineData("EUR", "CRC")]
    [InlineData("USD", "EUR")]
    public async Task OnlyUsdToCrc_IsPublished(string from, string to)
    {
        var (client, http, _) = Harness();

        await Assert.ThrowsAsync<ExchangeRateUnavailableException>(() => client.GetQuoteAsync(from, to));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task NoUrl_IsUnavailable_WithoutACall()
    {
        var (client, http, _) = Harness(url: "");

        await Assert.ThrowsAsync<ExchangeRateUnavailableException>(() => client.GetQuoteAsync("USD", "CRC"));
        Assert.Empty(http.Requests);
    }
}
