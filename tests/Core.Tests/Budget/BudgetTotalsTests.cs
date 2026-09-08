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
    public void PlannedByMethod_SplitsTheLinesByHowTheyArePaid_CardFirst()
    {
        var account = Line(250_000m, 0m); account.PaymentMethod = "bank_account";
        var accountUsd = Line(0m, 18.99m); accountUsd.PaymentMethod = "bank_account";

        var byMethod = BudgetTotals.PlannedByMethod([Line(60_000m, 0m), account, accountUsd], 500m);

        Assert.Equal(["credit_card", "bank_account"], byMethod.Select(m => m.Key));
        Assert.Equal((60_000m, 120m), (byMethod[0].TotalCrc, byMethod[0].TotalUsd));
        Assert.Equal((259_495m, 518.99m), (byMethod[1].TotalCrc, byMethod[1].TotalUsd));
        Assert.Single(BudgetTotals.PlannedByMethod([Line(1_000m, 0m)], 500m)); // a method with no line is absent
    }

    [Fact]
    public void ZeroRate_NeverDivides_AndAnEmptyPlanIsZero()
    {
        Assert.Equal(new MoneyPair(60_000m, 0m), BudgetTotals.Pair(Line(60_000m, 0m), 0m));
        Assert.Equal(MoneyPair.Zero, BudgetTotals.Planned([], 500m));
    }
}
