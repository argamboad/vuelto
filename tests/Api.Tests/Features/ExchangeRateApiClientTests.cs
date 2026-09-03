using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Core.Budget;
using Vuelto.Infrastructure.ExchangeRate;

namespace Vuelto.Api.Tests.Features;

/// <summary>
/// FX-1 provider client (donor US-014 / US-034 behaviour): a fresh cache counts as live and makes no
/// call, a stale cache refetches, a failed refresh serves the stale value flagged not-live with its
/// original timestamp, failures are never cached, a non-positive or missing rate is unavailable, an
/// unconfigured key is unavailable WITHOUT a call, and only ISO codes may enter the URL (R76).
/// </summary>
public class ExchangeRateApiClientTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Scriptable handler: the next responses in order (the last one repeats), every request recorded.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
        private (HttpStatusCode Status, string Body) _last = (HttpStatusCode.OK, Success(510.45m));
        public List<Uri> Requests { get; } = [];

        public StubHandler Then(HttpStatusCode status, string body) { _responses.Enqueue((status, body)); return this; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            if (_responses.Count > 0) _last = _responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(_last.Status) { Content = new StringContent(_last.Body) });
        }
    }

    private static string Success(decimal rate) => $$"""{"result":"success","conversion_rate":{{rate}}}""";

    private static (ExchangeRateApiClient Client, StubHandler Http, FakeTimeProvider Clock) Harness(string? apiKey = "test-key", int freshness = 60)
    {
        var http = new StubHandler();
        var clock = new FakeTimeProvider(T0);
        var client = new ExchangeRateApiClient(
            new HttpClient(http),
            new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new ExchangeRateSettings { ApiKey = apiKey, BaseUrl = "https://rates.test/v6", FreshnessMinutes = freshness }),
            clock,
            NullLogger<ExchangeRateApiClient>.Instance);
        return (client, http, clock);
    }

    [Fact]
    public async Task Quote_ParsesTheRate_IsLive_AndBuildsTheVendorUrl()
    {
        var (client, http, _) = Harness();

        var quote = await client.GetQuoteAsync("usd", "crc");

        Assert.Equal(510.45m, quote.Rate);
        Assert.True(quote.IsLive);
        Assert.Equal(T0, quote.AsOf);
        Assert.Equal("https://rates.test/v6/test-key/pair/USD/CRC", Assert.Single(http.Requests).ToString());
    }

    [Fact]
    public async Task FreshCache_IsServedAsLive_WithoutACall()
    {
        var (client, http, clock) = Harness();

        var first = await client.GetQuoteAsync("USD", "CRC");
        clock.Advance(TimeSpan.FromMinutes(30));
        var second = await client.GetQuoteAsync("USD", "CRC");

        Assert.Equal(first.Rate, second.Rate);
        Assert.True(second.IsLive);
        Assert.Equal(T0, second.AsOf); // the cached fetch time, not "now"
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task StaleCache_Refetches()
    {
        var (client, http, clock) = Harness();
        http.Then(HttpStatusCode.OK, Success(510.45m)).Then(HttpStatusCode.OK, Success(512m));

        await client.GetQuoteAsync("USD", "CRC");
        clock.Advance(TimeSpan.FromMinutes(61));
        var quote = await client.GetQuoteAsync("USD", "CRC");

        Assert.Equal(512m, quote.Rate);
        Assert.True(quote.IsLive);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task FailedRefresh_ServesTheStaleRate_NotLive_WithItsOriginalTimestamp()
    {
        var (client, http, clock) = Harness();
        http.Then(HttpStatusCode.OK, Success(510.45m)).Then(HttpStatusCode.ServiceUnavailable, "");

        await client.GetQuoteAsync("USD", "CRC");
        clock.Advance(TimeSpan.FromHours(3));
        var quote = await client.GetQuoteAsync("USD", "CRC");

        Assert.Equal(510.45m, quote.Rate);
        Assert.False(quote.IsLive);
        Assert.Equal(T0, quote.AsOf);
    }

    [Fact]
    public async Task Pairs_AreCachedSeparately()
    {
        var (client, http, _) = Harness();

        await client.GetQuoteAsync("USD", "CRC");
        await client.GetQuoteAsync("CRC", "USD");

        Assert.Equal(2, http.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "")]
    [InlineData(HttpStatusCode.OK, """{"result":"error","error-type":"invalid-key"}""")]
    [InlineData(HttpStatusCode.OK, """{"result":"success"}""")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task ProviderFailure_WithNoCache_IsUnavailable(HttpStatusCode status, string body)
    {
        var (client, http, _) = Harness();
        http.Then(status, body);

        await Assert.ThrowsAsync<ExchangeRateUnavailableException>(() => client.GetQuoteAsync("USD", "CRC"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-510.45)]
    public async Task NonPositiveRate_IsUnavailable(double rate)
    {
        var (client, http, _) = Harness();
        http.Then(HttpStatusCode.OK, Success((decimal)rate));

        await Assert.ThrowsAsync<ExchangeRateUnavailableException>(() => client.GetQuoteAsync("USD", "CRC"));
    }

    [Fact]
    public async Task Failures_AreNotCached_AndRecoveryIsLiveAgain()
    {
        var (client, http, _) = Harness();
        http.Then(HttpStatusCode.ServiceUnavailable, "").Then(HttpStatusCode.ServiceUnavailable, "").Then(HttpStatusCode.OK, Success(509.10m));

        await Assert.ThrowsAsync<ExchangeRateUnavailableException>(() => client.GetQuoteAsync("USD", "CRC"));
        await Assert.ThrowsAsync<ExchangeRateUnavailableException>(() => client.GetQuoteAsync("USD", "CRC"));
        var quote = await client.GetQuoteAsync("USD", "CRC");

        Assert.Equal(3, http.Requests.Count);
        Assert.Equal(509.10m, quote.Rate);
        Assert.True(quote.IsLive);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NoApiKey_IsUnavailable_WithoutACall(string? apiKey)
    {
        var (client, http, _) = Harness(apiKey);

        await Assert.ThrowsAsync<ExchangeRateUnavailableException>(() => client.GetQuoteAsync("USD", "CRC"));
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("USD/../admin")]
    [InlineData("₡")]
    public async Task OnlyIsoCodes_MayEnterTheUrl(string code)
    {
        var (client, http, _) = Harness();

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetQuoteAsync(code, "CRC"));
        Assert.Empty(http.Requests);
    }
}
