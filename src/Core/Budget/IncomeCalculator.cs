using Vuelto.Core.Entities;

namespace Vuelto.Core.Budget;

/// <summary>
/// The month's income as a dual-currency pair (ADR-V006): each configured income converted at the
/// rate passed in, plus every <c>inflow</c> transaction's frozen amounts (money in — realized refunds,
/// sales). One definition shared by the dashboard (DASH-1) and the reports (REPORTS-3), so "income" can
/// never mean two different numbers on two pages. Pure, no I/O; outputs 2 dp.
/// </summary>
public static class IncomeCalculator
{
    public static IncomeSummary Calculate(Month month, IReadOnlyList<Transaction> transactions, FxRates rates)
    {
        var primary = IncomePair(month.PrimaryIncomeAmount, month.PrimaryIncomeCurrency, rates);
        var secondary = IncomePair(month.SecondaryIncomeAmount, month.SecondaryIncomeCurrency, rates);
        var inflows = transactions.Where(t => string.Equals(t.TransactionType, TransactionTypes.Inflow, StringComparison.Ordinal)).ToList();
        var total = Pair(primary.Crc + secondary.Crc + inflows.Sum(t => t.AmountCrc), primary.Usd + secondary.Usd + inflows.Sum(t => t.AmountUsd));
        return new IncomeSummary(primary, secondary, total);
    }

    // Income converts at the rate the household would GET (ADR-V019): dollars sold at Buy, colones buying dollars at Sell.
    private static MoneyPair IncomePair(decimal amount, string currency, FxRates rates) =>
        string.Equals(currency, Currencies.Crc, StringComparison.Ordinal)
            ? Pair(amount, rates.IncomeCrcToUsd(amount))
            : Pair(rates.IncomeUsdToCrc(amount), amount);

    private static MoneyPair Pair(decimal crc, decimal usd) => new(CurrencyMath.Round2(crc), CurrencyMath.Round2(usd));
}
