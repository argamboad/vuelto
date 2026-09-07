using Vuelto.Core.Budget;

namespace Vuelto.Core.Tests.Budget;

/// <summary>
/// ADR-V019 — the one direction rule for the day's buy/sell pair. BCCR on 2026-09-07: compra 448.27 (what the
/// bank pays for a dollar), venta 453.69 (what a dollar costs). Spending converts at the rate you would pay to
/// fund it; income at the rate you would get; a single rate collapses every rule to the old behaviour.
/// </summary>
public class FxRatesTests
{
    private static readonly FxRates Bccr = new(Buy: 448.27m, Sell: 453.69m);

    [Fact]
    public void Spending_UsdPurchaseCostsColonesAtSell_CrcPurchaseCostsDollarsAtBuy()
    {
        Assert.Equal(453.69m, Bccr.ForSpend("USD"));   // the rate frozen on a $ voucher
        Assert.Equal(448.27m, Bccr.ForSpend("CRC"));   // the rate frozen on a ₡ voucher
        Assert.Equal(448.27m, Bccr.ForSpend("crc")); // case-insensitive

        Assert.Equal(4536.90m, Bccr.SpendUsdToCrc(10m));
        Assert.Equal(10m, Math.Round(Bccr.SpendCrcToUsd(4482.70m), 4));
    }

    [Fact]
    public void Income_UsdYieldsColonesAtBuy_CrcBuysDollarsAtSell()
    {
        Assert.Equal(448.27m, Bccr.ForIncome("USD"));
        Assert.Equal(453.69m, Bccr.ForIncome("CRC"));

        Assert.Equal(4_012_016.50m, Bccr.IncomeUsdToCrc(8_950m)); // the owner's $8,950 at compra (× 448.27) — not at venta
        Assert.Equal(1_000m, Math.Round(Bccr.IncomeCrcToUsd(453_690m), 4));
    }

    [Fact]
    public void SingleRate_MakesEveryRuleTheOldOne()
    {
        FxRates one = 500m; // implicit — keeps single-rate callers and tests unchanged

        Assert.Equal(new FxRates(500m, 500m), one);
        Assert.Equal(500m, one.ForSpend("USD"));
        Assert.Equal(500m, one.ForIncome("CRC"));
        Assert.Equal(2m, one.SpendCrcToUsd(1_000m));
        Assert.Equal(2m, one.IncomeCrcToUsd(1_000m));
    }

    [Fact]
    public void ZeroRates_NeverDivide()
    {
        var zero = FxRates.Single(0m);

        Assert.Equal(0m, zero.SpendCrcToUsd(1_000m));
        Assert.Equal(0m, zero.IncomeCrcToUsd(1_000m));
    }
}
