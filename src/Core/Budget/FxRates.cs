namespace Vuelto.Core.Budget;

/// <summary>
/// The day's two USD→CRC rates and the ONE rule for which applies (ADR-V019). <see cref="Buy"/> is what
/// the bank pays you for a dollar (BCCR <i>compra</i>); <see cref="Sell"/> is what a dollar costs you
/// (<i>venta</i>). The rule follows the money the household would actually move:
/// <list type="bullet">
/// <item><b>Spending</b> — a purchase is expressed in the other currency at the rate you would pay to fund
/// it: a <b>USD purchase</b> costs colones at <see cref="Sell"/>; a <b>CRC purchase</b> costs dollars at
/// <see cref="Buy"/> (you sell dollars to get the colones).</item>
/// <item><b>Income</b> — the mirror image: <b>USD income</b> yields colones at <see cref="Buy"/>; <b>CRC income</b>
/// buys dollars at <see cref="Sell"/>.</item>
/// <item><b>Budget lines</b> are spending targets, so they convert like spending.</item>
/// </list>
/// A single rate (a manual override, the last-transaction tier, the world-feed provider) is
/// <see cref="Single"/>: both sides equal, and every rule collapses to the old one-rate behaviour.
/// </summary>
public sealed record FxRates(decimal Buy, decimal Sell)
{
    public static FxRates Single(decimal rate) => new(rate, rate);

    /// <summary>A bare rate means "one rate for both" — keeps single-rate callers and tests unchanged.</summary>
    public static implicit operator FxRates(decimal rate) => Single(rate);

    /// <summary>The one rate frozen on a transaction in <paramref name="currency"/> (<see cref="Transaction.ExchangeRateUsed"/>).</summary>
    public decimal ForSpend(string currency) => IsCrc(currency) ? Buy : Sell;

    /// <summary>The rate that converts income earned in <paramref name="currency"/>.</summary>
    public decimal ForIncome(string currency) => IsCrc(currency) ? Sell : Buy;

    public decimal SpendUsdToCrc(decimal usd) => usd * Sell;
    public decimal SpendCrcToUsd(decimal crc) => Buy == 0 ? 0 : crc / Buy;
    public decimal IncomeUsdToCrc(decimal usd) => usd * Buy;
    public decimal IncomeCrcToUsd(decimal crc) => Sell == 0 ? 0 : crc / Sell;

    private static bool IsCrc(string currency) => string.Equals(currency, Currencies.Crc, StringComparison.OrdinalIgnoreCase);
}
