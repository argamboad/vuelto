using Vuelto.Core.Entities;

namespace Vuelto.Core.Budget;

/// <summary>What a card is called and what kind of plastic it is — the labels a spend cut needs (CARDS-2/3).</summary>
public record CardLabel(string Name, string Kind);

/// <summary>Spend on one card over a set of rows (CARDS-2). <c>CardId</c> null = the "no card" bucket (cash, transfers, rows from before CARDS-1).</summary>
public record CardSpendEntry(Guid? CardId, string CardName, string CardKind, decimal TotalCrc, decimal TotalUsd, int Count);

/// <summary>
/// CARDS-2: "how much went through each card?" — the expense-class rows (budgeted, extraordinary, unplanned essential;
/// never inflows or envelope contributions) grouped by the card that paid, frozen amounts summed, largest first, the
/// "no card" bucket last. Names come from the all-states catalog so an inactive card still labels its history. Pure;
/// the dashboard (one month) and the Reports analysis (any period) both call it.
/// </summary>
public static class CardSpend
{
    public static IReadOnlyList<CardSpendEntry> Calculate(IEnumerable<Transaction> transactions, IReadOnlyDictionary<Guid, CardLabel>? cards)
    {
        var labels = cards ?? new Dictionary<Guid, CardLabel>();
        return transactions
            .Where(t => TransactionTypes.Expenses.Contains(t.TransactionType))
            .GroupBy(t => t.CardId)
            .Select(g =>
            {
                var label = g.Key is { } id ? labels.GetValueOrDefault(id) : null;
                return new CardSpendEntry(g.Key, label?.Name ?? "", label?.Kind ?? CardKinds.Credit,
                    CurrencyMath.Round2(g.Sum(t => t.AmountCrc)), CurrencyMath.Round2(g.Sum(t => t.AmountUsd)), g.Count());
            })
            .OrderBy(e => e.CardId is null).ThenByDescending(e => e.TotalCrc).ThenBy(e => e.CardName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
