using Vuelto.Core.Entities;

namespace Vuelto.Core.Budget;

/// <summary>
/// The month's income as a dual-currency pair (ADR-V006, ADR-V023): each of the month's income rows converted at the
/// rate passed in, plus every <c>inflow</c> transaction's frozen amounts (money in — realized refunds, sales). One
/// definition shared by the dashboard (DASH-1), the reports (REPORTS-3/4) and the PDF, so "income" can never mean two
/// different numbers on two pages. Pure, no I/O; outputs 2 dp.
/// </summary>
public static class IncomeCalculator
{
    public static IncomeSummary Calculate(IReadOnlyList<MonthIncome> rows, IReadOnlyList<Transaction> transactions, FxRates rates)
    {
        var lines = rows
            .OrderBy(r => r.SortOrder)
            .Select(r => new IncomeRowSummary(r.Id, r.Label, r.MemberUserId, r.Currency, r.Amount, r.PlannedAmount, IncomePair(r.Amount, r.Currency, rates)))
            .ToList();
        var inflows = transactions.Where(t => string.Equals(t.TransactionType, TransactionTypes.Inflow, StringComparison.Ordinal)).ToList();
        var inflowPair = Pair(inflows.Sum(t => t.AmountCrc), inflows.Sum(t => t.AmountUsd));
        var total = Pair(lines.Sum(l => l.Pair.Crc) + inflowPair.Crc, lines.Sum(l => l.Pair.Usd) + inflowPair.Usd);
        return new IncomeSummary(lines, inflowPair, total);
    }

    // Income converts at the rate the household would GET (ADR-V019): dollars sold at Buy, colones buying dollars at Sell.
    private static MoneyPair IncomePair(decimal amount, string currency, FxRates rates) =>
        string.Equals(currency, Currencies.Crc, StringComparison.Ordinal)
            ? Pair(amount, rates.IncomeCrcToUsd(amount))
            : Pair(rates.IncomeUsdToCrc(amount), amount);

    private static MoneyPair Pair(decimal crc, decimal usd) => new(CurrencyMath.Round2(crc), CurrencyMath.Round2(usd));
}
