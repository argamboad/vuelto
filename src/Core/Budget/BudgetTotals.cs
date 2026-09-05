namespace Vuelto.Core.Budget;

/// <summary>
/// A budget line is set in ONE currency (its other side is 0 — EXPENSES-1). To compare lines with income
/// or with each other in a single currency they are converted at the rate passed in (ADR-V006, a
/// projection). One definition shared by the dashboard's budgeted-vs-actual column and the reports'
/// "Income vs budget" donut. Pure; outputs 2 dp.
/// </summary>
public static class BudgetTotals
{
    /// <summary>One line as a dual-currency pair: its native side kept, the other side converted.</summary>
    public static MoneyPair Pair(IExpenseLine line, decimal rate) =>
        line.BudgetCrc > 0
            ? new(CurrencyMath.Round2(line.BudgetCrc), CurrencyMath.Round2(rate == 0 ? 0 : line.BudgetCrc / rate))
            : new(CurrencyMath.Round2(line.BudgetUsd * rate), CurrencyMath.Round2(line.BudgetUsd));

    /// <summary>The planned month: every line given (callers pass the ACTIVE ones), converted and summed.</summary>
    public static MoneyPair Planned(IEnumerable<IExpenseLine> lines, decimal rate)
    {
        decimal crc = 0, usd = 0;
        foreach (var line in lines)
        {
            var p = Pair(line, rate);
            crc += p.Crc; usd += p.Usd;
        }
        return new(CurrencyMath.Round2(crc), CurrencyMath.Round2(usd));
    }
}
