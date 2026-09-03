using System.Net;
using Bunit;
using Vuelto.Shared.Ui.Components;
using Vuelto.Shared.Ui.Pages;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>FX-1 badge: one look tells the member whether today's rate is live, stale, borrowed from a transaction, or missing.</summary>
public class ExchangeRateBadgeTests : ComponentTestBase
{
    private const string Live = """{"rate":510.45,"source":"live","as_of":"2026-09-03T12:00:00+00:00"}""";
    private const string Stale = """{"rate":508.00,"source":"cache","as_of":"2026-09-03T07:00:00+00:00"}""";
    private const string FromTx = """{"rate":505.20,"source":"transaction","as_of":"2026-09-01T09:30:00+00:00"}""";

    [Fact]
    public void LiveRate_ShowsTheRate_AndTheLiveBadge()
    {
        Http.On(HttpMethod.Get, "/api/exchange-rate", Live);

        var cut = Render<ExchangeRateBadge>();

        cut.WaitForAssertion(() => Assert.Contains("510.45", cut.Find("[data-testid='fx-rate']").TextContent));
        Assert.Contains("Fx_Live", cut.Find("[data-testid='fx-live']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='fx-unavailable']"));
    }

    [Fact]
    public void StaleRate_IsFlaggedAsOf()
    {
        Http.On(HttpMethod.Get, "/api/exchange-rate", Stale);

        var cut = Render<ExchangeRateBadge>();

        cut.WaitForAssertion(() => Assert.Contains("Fx_AsOf[", cut.Find("[data-testid='fx-stale']").TextContent));
        Assert.Empty(cut.FindAll("[data-testid='fx-live']"));
    }

    [Fact]
    public void TransactionRate_SaysWhereItCameFrom()
    {
        Http.On(HttpMethod.Get, "/api/exchange-rate", FromTx);

        var cut = Render<ExchangeRateBadge>();

        cut.WaitForAssertion(() => Assert.Contains("Fx_FromTransaction[", cut.Find("[data-testid='fx-transaction']").TextContent));
    }

    [Fact]
    public void Unavailable_ShowsTheHonestMessage_NotAFabricatedRate()
    {
        Http.On(HttpMethod.Get, "/api/exchange-rate",
            """{"error":"exchange_rate_unavailable","message":"No exchange rate available"}""", HttpStatusCode.ServiceUnavailable);

        var cut = Render<ExchangeRateBadge>();

        cut.WaitForAssertion(() => Assert.Contains("Fx_Unavailable", cut.Find("[data-testid='fx-unavailable']").TextContent));
        Assert.Empty(cut.FindAll("[data-testid='fx-rate']"));
    }

    [Fact]
    public async Task Home_MountsTheBadge_ForASignedInMember()
    {
        await SignInAsync();
        Http.On(HttpMethod.Get, "/api/exchange-rate", Live);

        var cut = Render<Home>();

        cut.WaitForAssertion(() => Assert.Contains("510.45", cut.Find("[data-testid='fx-rate']").TextContent));
    }
}
