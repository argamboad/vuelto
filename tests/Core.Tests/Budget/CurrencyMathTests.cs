using Vuelto.Core.Budget;

namespace Vuelto.Core.Tests.Budget;

/// <summary>ADR-V004: both amounts derive from the original + currency + rate, 2 dp half away from zero; a non-positive rate never derives.</summary>
public class CurrencyMathTests
{
    [Theory]
    [InlineData(10_000, "CRC", 500, 10_000, 20)]
    [InlineData(20, "USD", 500, 10_000, 20)]
    [InlineData(1_000, "crc", 512.345, 1_000, 1.95)]      // 1.9518… → 1.95; lower-case currency accepted
    [InlineData(1.005, "USD", 500, 502.5, 1.01)]           // half away from zero on the original too
    [InlineData(333, "CRC", 3, 333, 111)]
    public void DeriveAmounts_ComputesBothSides(double original, string currency, double rate, double crc, double usd)
    {
        var (amountCrc, amountUsd) = CurrencyMath.DeriveAmounts((decimal)original, currency, (decimal)rate);
        Assert.Equal((decimal)crc, amountCrc);
        Assert.Equal((decimal)usd, amountUsd);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DeriveAmounts_NonPositiveRate_IsUnavailable(double rate) =>
        Assert.Throws<ExchangeRateUnavailableException>(() => CurrencyMath.DeriveAmounts(100m, "USD", (decimal)rate));

    [Fact]
    public void DeriveAmounts_UnknownCurrency_IsRejected() =>
        Assert.Throws<ArgumentException>(() => CurrencyMath.DeriveAmounts(100m, "EUR", 500m));

    [Theory]
    [InlineData(1.005, 1.01)]
    [InlineData(-1.005, -1.01)]
    [InlineData(2.675, 2.68)]
    public void Round2_IsHalfAwayFromZero(double value, double expected) =>
        Assert.Equal((decimal)expected, CurrencyMath.Round2((decimal)value));
}
