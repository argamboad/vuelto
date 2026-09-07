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
    // A line is a spending target, so it converts like spending (ADR-V019): a colón line costs dollars at Buy, a dollar line costs colones at Sell.
    public static MoneyPair Pair(IExpenseLine line, FxRates rates) =>
        line.BudgetCrc > 0
            ? new(CurrencyMath.Round2(line.BudgetCrc), CurrencyMath.Round2(rates.SpendCrcToUsd(line.BudgetCrc)))
            : new(CurrencyMath.Round2(rates.SpendUsdToCrc(line.BudgetUsd)), CurrencyMath.Round2(line.BudgetUsd));

    /// <summary>The planned month: every line given (callers pass the ACTIVE ones), converted and summed.</summary>
    public static MoneyPair Planned(IEnumerable<IExpenseLine> lines, FxRates rates)
    {
        decimal crc = 0, usd = 0;
        foreach (var line in lines)
        {
            var p = Pair(line, rates);
            crc += p.Crc; usd += p.Usd;
        }
        return new(CurrencyMath.Round2(crc), CurrencyMath.Round2(usd));
    }
}
