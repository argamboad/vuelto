using Vuelto.Core.Entities;

namespace Vuelto.Core.Budget;

/// <summary>
/// REPORTS-1 (donor US-043): groups the spending classes (<c>budgeted</c>, <c>extraordinary</c>,
/// <c>unplanned_essential</c>) by category over the transactions the caller already filtered to the
/// period. Pure. Amounts are the frozen per-transaction amounts, never recomputed. When
/// <paramref name="activeLines"/> is supplied the period is a single anchor month and each budgeted
/// entry carries the catalog budget for its category (sum of every active line, null when none) —
/// a monthly budget does not multiply cleanly across arbitrary ranges, so multi-month omits it.
/// Zero-spend categories are absent; entries sort by category name.
/// </summary>
public static class CategoryAnalysisCalculator
{
    public static CategoryAnalysis Calculate(
        IReadOnlyList<Transaction> transactionsInPeriod,
        IReadOnlyDictionary<Guid, string> categoryNames,
        DateOnly from,
        DateOnly to,
        IReadOnlyList<IExpenseLine>? activeLines)
    {
        var singleMonth = activeLines is not null;
        var budgetByCategory = new Dictionary<Guid, (decimal Crc, decimal Usd)>();
        if (activeLines is not null)
        {
            foreach (var line in activeLines.Where(l => l.IsActive))
            {
                var acc = budgetByCategory.GetValueOrDefault(line.CategoryId);
                budgetByCategory[line.CategoryId] = (acc.Crc + line.BudgetCrc, acc.Usd + line.BudgetUsd);
            }
        }

        List<CategorySpendEntry> ForClass(string type, bool decorate) =>
            transactionsInPeriod
                .Where(t => string.Equals(t.TransactionType, type, StringComparison.Ordinal))
                .GroupBy(t => t.CategoryId)
                .Select(g =>
                {
                    decimal? bCrc = null, bUsd = null;
                    if (decorate && budgetByCategory.TryGetValue(g.Key, out var b)) { bCrc = b.Crc; bUsd = b.Usd; }
                    return new CategorySpendEntry(g.Key, categoryNames.GetValueOrDefault(g.Key, ""),
                        CurrencyMath.Round2(g.Sum(t => t.AmountCrc)), CurrencyMath.Round2(g.Sum(t => t.AmountUsd)), bCrc, bUsd);
                })
                .OrderBy(e => e.CategoryName, StringComparer.OrdinalIgnoreCase)
                .ToList();

        return new CategoryAnalysis(from, to, singleMonth,
            ForClass(TransactionTypes.Budgeted, singleMonth),
            ForClass(TransactionTypes.Extraordinary, false),
            ForClass(TransactionTypes.UnplannedEssential, false));
    }
}
