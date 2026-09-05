using Vuelto.Core.Budget;
using Vuelto.Core.Entities;

namespace Vuelto.Core.Tests.Budget;

/// <summary>
/// REPORTS-3 / DASH-1: a budget line is single-currency; converting at the passed-in rate gives the
/// pair both pages use, and the planned month is the sum of the (active) lines given.
/// </summary>
public class BudgetTotalsTests
{
    private static FixedExpense Line(decimal crc, decimal usd) => new()
    {
        TenantId = Guid.CreateVersion7(), Name = "line", CategoryId = Guid.CreateVersion7(), PaymentMethod = "credit_card", BudgetCrc = crc, BudgetUsd = usd,
    };

    [Fact]
    public void ColonLine_KeepsItsColones_AndConvertsToDollars()
    {
        Assert.Equal(new MoneyPair(60_000m, 120m), BudgetTotals.Pair(Line(60_000m, 0m), 500m));
    }

    [Fact]
    public void DollarLine_KeepsItsDollars_AndConvertsToColones_Rounded()
    {
        Assert.Equal(new MoneyPair(9_495m, 18.99m), BudgetTotals.Pair(Line(0m, 18.99m), 500m));
    }

    [Fact]
    public void Planned_SumsMixedLines_InBothCurrencies()
    {
        var planned = BudgetTotals.Planned([Line(60_000m, 0m), Line(0m, 18.99m), Line(250_000m, 0m)], 500m);

        Assert.Equal(new MoneyPair(319_495m, 638.99m), planned);
    }

    [Fact]
    public void ZeroRate_NeverDivides_AndAnEmptyPlanIsZero()
    {
        Assert.Equal(new MoneyPair(60_000m, 0m), BudgetTotals.Pair(Line(60_000m, 0m), 0m));
        Assert.Equal(MoneyPair.Zero, BudgetTotals.Planned([], 500m));
    }
}
