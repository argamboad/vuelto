using Vuelto.Core.Budget;
using Vuelto.Core.Entities;

namespace Vuelto.Core.Tests.Budget;

/// <summary>
/// REPORTS-3 / DASH-1 / INCOME-1: the one income definition every page uses — the month's income rows converted at
/// the passed-in rate (each in its own currency), plus inflows' frozen amounts, 2 dp.
/// </summary>
public class IncomeCalculatorTests
{
    private static MonthIncome Row(string label, decimal amount, string currency, int order = 0, decimal? planned = null) => new()
    {
        TenantId = Guid.CreateVersion7(), MonthId = Guid.CreateVersion7(), Label = label, Amount = amount, Currency = currency,
        SortOrder = order, PlannedAmount = planned,
    };

    /// <summary>The old two-slot month as rows — the shape every existing month migrated into.</summary>
    private static List<MonthIncome> MonthWith(decimal primary, string primaryCurrency, decimal secondary, string secondaryCurrency) =>
        [Row("Primary income", primary, primaryCurrency, 0), Row("Secondary income", secondary, secondaryCurrency, 1)];

    private static Transaction Tx(string type, decimal crc, decimal usd) => new()
    {
        TenantId = Guid.CreateVersion7(), Payee = "x", TransactionType = type, AmountCrc = crc, AmountUsd = usd, OriginalAmount = crc, Currency = "CRC", ExchangeRateUsed = 500m,
        TransactionDate = new DateOnly(2026, 9, 1), PaymentMethod = "credit_card",
    };

    [Fact]
    public void UsdIncomes_ConvertToColonesAtTheRate()
    {
        var total = IncomeCalculator.Calculate(MonthWith(3000m, "USD", 500m, "USD"), [], 500m).Total;

        Assert.Equal((1_750_000m, 3500m), (total.Crc, total.Usd));
    }

    [Fact]
    public void CrcIncome_ConvertsToDollarsAtTheRate_AndMixedCurrenciesAdd()
    {
        var income = IncomeCalculator.Calculate(MonthWith(1_500_000m, "CRC", 500m, "USD"), [], 500m);

        Assert.Equal((1_500_000m, 3000m), (income.Rows[0].Pair.Crc, income.Rows[0].Pair.Usd));
        Assert.Equal((250_000m, 500m), (income.Rows[1].Pair.Crc, income.Rows[1].Pair.Usd));
        Assert.Equal((1_750_000m, 3500m), (income.Total.Crc, income.Total.Usd));
    }

    [Fact]
    public void Inflows_FoldIntoTheTotal_WithTheirFrozenAmounts_OtherClassesDoNot()
    {
        var rows = new List<Transaction>
        {
            Tx("inflow", 9_000m, 18m),            // a realized refund
            Tx("budgeted", 50_000m, 100m),        // spending — not income
            Tx("envelope_contribution", 10_000m, 20m),
        };

        var total = IncomeCalculator.Calculate(MonthWith(1_000_000m, "CRC", 0m, "USD"), rows, 500m).Total;

        Assert.Equal((1_009_000m, 2018m), (total.Crc, total.Usd));
    }

    [Fact]
    public void ZeroRate_NeverDivides()
    {
        var total = IncomeCalculator.Calculate(MonthWith(1_000_000m, "CRC", 0m, "USD"), [], 0m).Total;

        Assert.Equal((1_000_000m, 0m), (total.Crc, total.Usd));
    }

    [Fact]
    public void Rows_KeepTheirOrderLabelAndPlan_AndInflowsAreTheirOwnFigure()
    {
        var member = Guid.CreateVersion7();
        List<MonthIncome> rows =
        [
            Row("Freelance", 420m, "USD", 1, planned: 300m),
            Row("Allan salary", 2_500m, "USD", 0, planned: 2_500m),
            Row("Sold the bike", 150_000m, "CRC", 2),
        ];
        rows[1].MemberUserId = member;

        var income = IncomeCalculator.Calculate(rows, [Tx("inflow", 9_000m, 18m)], 500m);

        Assert.Equal(["Allan salary", "Freelance", "Sold the bike"], income.Rows.Select(r => r.Label));
        Assert.Equal((member, "USD", 2_500m, (decimal?)2_500m), (income.Rows[0].MemberUserId!.Value, income.Rows[0].Currency, income.Rows[0].Amount, income.Rows[0].PlannedAmount));
        Assert.Equal((420m, (decimal?)300m), (income.Rows[1].Amount, income.Rows[1].PlannedAmount)); // corrected: the plan stays visible
        Assert.Null(income.Rows[2].PlannedAmount);                                                  // a one-off
        Assert.Equal(new MoneyPair(9_000m, 18m), income.Inflows);
        // $2,920 at 500 = ₡1,460,000; ₡150,000 = $300; + the inflow.
        Assert.Equal(new MoneyPair(1_619_000m, 3_238m), income.Total);
    }

    [Fact]
    public void NoRows_IsJustTheInflows() =>
        Assert.Equal(new MoneyPair(9_000m, 18m), IncomeCalculator.Calculate([], [Tx("inflow", 9_000m, 18m)], 500m).Total);
}
