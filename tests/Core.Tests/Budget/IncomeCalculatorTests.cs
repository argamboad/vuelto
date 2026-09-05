using Vuelto.Core.Budget;
using Vuelto.Core.Entities;

namespace Vuelto.Core.Tests.Budget;

/// <summary>
/// REPORTS-3 / DASH-1: the one income definition both pages use — configured incomes converted at the
/// passed-in rate (each in its own currency), plus inflows' frozen amounts, 2 dp.
/// </summary>
public class IncomeCalculatorTests
{
    private static Month MonthWith(decimal primary, string primaryCurrency, decimal secondary, string secondaryCurrency) => new()
    {
        TenantId = Guid.CreateVersion7(), Year = 2026, MonthNumber = 9, WeekCount = 5, Week1StartDate = new DateOnly(2026, 8, 25),
        PrimaryIncomeAmount = primary, PrimaryIncomeCurrency = primaryCurrency,
        SecondaryIncomeAmount = secondary, SecondaryIncomeCurrency = secondaryCurrency,
    };

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

        Assert.Equal((1_500_000m, 3000m), (income.Primary.Crc, income.Primary.Usd));
        Assert.Equal((250_000m, 500m), (income.Secondary.Crc, income.Secondary.Usd));
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
}
