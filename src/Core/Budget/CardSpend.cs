using Vuelto.Core.Entities;

namespace Vuelto.Core.Budget;

/// <summary>Spend on one card over a set of rows (CARDS-2). <c>CardId</c> null = the "no card" bucket (cash, transfers, rows from before CARDS-1).</summary>
public record CardSpendEntry(Guid? CardId, string CardName, decimal TotalCrc, decimal TotalUsd, int Count);

/// <summary>
/// CARDS-2: "how much went through each card?" — the expense-class rows (budgeted, extraordinary, unplanned essential;
/// never inflows or envelope contributions) grouped by the card that paid, frozen amounts summed, largest first, the
/// "no card" bucket last. Names come from the all-states catalog so an inactive card still labels its history. Pure;
/// the dashboard (one month) and the Reports analysis (any period) both call it.
/// </summary>
public static class CardSpend
{
    public static IReadOnlyList<CardSpendEntry> Calculate(IEnumerable<Transaction> transactions, IReadOnlyDictionary<Guid, string>? cardNames)
    {
        var names = cardNames ?? new Dictionary<Guid, string>();
        return transactions
            .Where(t => TransactionTypes.Expenses.Contains(t.TransactionType))
            .GroupBy(t => t.CardId)
            .Select(g => new CardSpendEntry(g.Key, g.Key is { } id ? names.GetValueOrDefault(id, "") : "",
                CurrencyMath.Round2(g.Sum(t => t.AmountCrc)), CurrencyMath.Round2(g.Sum(t => t.AmountUsd)), g.Count()))
            .OrderBy(e => e.CardId is null).ThenByDescending(e => e.TotalCrc).ThenBy(e => e.CardName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
