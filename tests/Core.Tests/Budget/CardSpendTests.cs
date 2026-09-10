using Vuelto.Core.Budget;
using Vuelto.Core.Entities;

namespace Vuelto.Core.Tests.Budget;

/// <summary>CARDS-2: expense rows grouped by the card that paid — largest first, "no card" last, inflows and contributions out, inactive names kept.</summary>
public class CardSpendTests
{
    private static readonly Guid Visa = Guid.NewGuid(), Amex = Guid.NewGuid(), Cat = Guid.NewGuid();

    private static Transaction Tx(Guid? card, string type, decimal crc) => new()
    {
        TenantId = Guid.NewGuid(), MonthId = Guid.NewGuid(), BankId = Guid.NewGuid(), CategoryId = Cat, CardId = card, Payee = "x",
        OriginalAmount = crc, Currency = "CRC", TransactionDate = new DateOnly(2026, 9, 1), AmountCrc = crc, AmountUsd = crc / 500m, ExchangeRateUsed = 500m, TransactionType = type
    };

    [Fact]
    public void GroupsExpenseRowsByCard_LargestFirst_NoCardLast_InflowsExcluded()
    {
        var rows = CardSpend.Calculate(
        [
            Tx(Visa, "budgeted", 1_000m), Tx(Amex, "extraordinary", 5_000m), Tx(Visa, "unplanned_essential", 2_500m),
            Tx(null, "budgeted", 9_000m), Tx(Visa, "inflow", 90_000m), Tx(Amex, "envelope_contribution", 7_000m)
        ], new Dictionary<Guid, CardLabel> { [Visa] = new("Allan's Visa", CardKinds.Debit), [Amex] = new("Old Amex", CardKinds.Credit) });

        Assert.Equal(["Old Amex", "Allan's Visa", ""], rows.Select(r => r.CardName));
        Assert.Equal((Amex, 5_000m, 10m, 1), (rows[0].CardId, rows[0].TotalCrc, rows[0].TotalUsd, rows[0].Count));
        Assert.Equal((Visa, "debit", 3_500m, 2), (rows[1].CardId, rows[1].CardKind, rows[1].TotalCrc, rows[1].Count)); // the kind rides along for the dashboard column
        Assert.Equal((null, 9_000m), (rows[2].CardId, rows[2].TotalCrc)); // the "no card" bucket closes the list, whatever its size
    }

    [Fact]
    public void NothingSpent_IsEmpty_AndAnUnknownCardStillGetsARow()
    {
        Assert.Empty(CardSpend.Calculate([Tx(Visa, "inflow", 100m)], null));
        var unknown = Assert.Single(CardSpend.Calculate([Tx(Visa, "budgeted", 100m)], null));
        Assert.Equal((Visa, "", "credit"), (unknown.CardId, unknown.CardName, unknown.CardKind)); // an unknown card reads as credit, the default
    }
}
