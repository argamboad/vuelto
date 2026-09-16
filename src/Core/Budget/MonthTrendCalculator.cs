using Vuelto.Core.Entities;

namespace Vuelto.Core.Budget;

/// <summary>One month in the trend: its income (null when no rate could be resolved) and its spend (the three expense classes, frozen amounts).</summary>
public record MonthTrendEntry(Guid MonthId, int Year, int MonthNumber, MoneyPair? Income, MoneyPair Spend);

/// <summary>
/// REPORTS-4: month after month, income against spend, so "is the remaining growing or shrinking" is one
/// picture. Spend is each transaction's frozen amounts (ADR-V006); income is the shared
/// <see cref="IncomeCalculator"/> at the rate passed in (today's, a projection — the same number the
/// dashboard shows for that month today). Oldest first. Pure.
/// </summary>
public static class MonthTrendCalculator
{
    public static IReadOnlyList<MonthTrendEntry> Calculate(
        IReadOnlyList<Month> months, IReadOnlyList<MonthIncome> incomeRows, IReadOnlyList<Transaction> transactions, FxRates? rates)
    {
        var byMonth = transactions.ToLookup(t => t.MonthId);
        var incomeByMonth = incomeRows.ToLookup(r => r.MonthId);
        return months
            .OrderBy(m => m.Week1StartDate)
            .Select(m =>
            {
                var rows = byMonth[m.Id].ToList();
                var spend = rows.Where(t => TransactionTypes.Expenses.Contains(t.TransactionType)).ToList();
                return new MonthTrendEntry(m.Id, m.Year, m.MonthNumber,
                    rates is { } r ? IncomeCalculator.Calculate(incomeByMonth[m.Id].ToList(), rows, r).Total : null,
                    new MoneyPair(CurrencyMath.Round2(spend.Sum(t => t.AmountCrc)), CurrencyMath.Round2(spend.Sum(t => t.AmountUsd))));
            })
            .ToList();
    }
}
