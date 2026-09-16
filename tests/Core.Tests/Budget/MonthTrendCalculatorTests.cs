using Vuelto.Core.Budget;
using Vuelto.Core.Entities;

namespace Vuelto.Core.Tests.Budget;

/// <summary>REPORTS-4: months oldest first, spend = the three expense classes' frozen amounts per month, income = the shared calculator at the passed rate (null without a rate).</summary>
public class MonthTrendCalculatorTests
{
    private static Month M(int year, int month, DateOnly start) => new()
    {
        TenantId = Guid.Empty, Year = year, MonthNumber = month, WeekCount = 4, Week1StartDate = start,
    };

    private static MonthIncome Income(Month month, decimal usd) => new()
    {
        TenantId = Guid.Empty, MonthId = month.Id, Label = "Salary", Amount = usd, Currency = "USD",
    };

    private static Transaction Tx(Guid monthId, string type, decimal crc) => new()
    {
        TenantId = Guid.Empty, MonthId = monthId, BankId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), Payee = "x", TransactionType = type,
        OriginalAmount = crc, Currency = "CRC", AmountCrc = crc, AmountUsd = crc / 500m, ExchangeRateUsed = 500m, TransactionDate = new DateOnly(2026, 6, 1),
    };

    [Fact]
    public void OldestFirst_SpendPerMonth_IncomeAtTheRate_InflowsCountAsIncomeNotSpend()
    {
        var july = M(2026, 7, new DateOnly(2026, 6, 25));
        var june = M(2026, 6, new DateOnly(2026, 5, 28));
        List<MonthIncome> income = [Income(june, 1000m), Income(july, 1000m)];
        var rows = new List<Transaction>
        {
            Tx(june.Id, "budgeted", 100_000m), Tx(june.Id, "extraordinary", 50_000m), Tx(june.Id, "unplanned_essential", 25_000m),
            Tx(june.Id, "inflow", 9_000m), Tx(june.Id, "envelope_contribution", 20_000m),
            Tx(july.Id, "budgeted", 300_000m),
        };

        var trend = MonthTrendCalculator.Calculate([july, june], income, rows, 500m);

        Assert.Equal([6, 7], trend.Select(t => t.MonthNumber));
        Assert.Equal((175_000m, 350m), (trend[0].Spend.Crc, trend[0].Spend.Usd));          // inflow + envelope excluded
        Assert.Equal(new MoneyPair(509_000m, 1_018m), trend[0].Income);     // $1,000 @500 + the ₡9,000 inflow
        Assert.Equal((300_000m, 600m), (trend[1].Spend.Crc, trend[1].Spend.Usd));
        Assert.Equal(new MoneyPair(500_000m, 1_000m), trend[1].Income);
    }

    [Fact]
    public void NoRate_SpendStillComputed_IncomeNull()
    {
        var june = M(2026, 6, new DateOnly(2026, 5, 28));

        var entry = Assert.Single(MonthTrendCalculator.Calculate([june], [Income(june, 1000m)], [Tx(june.Id, "budgeted", 10_000m)], null));

        Assert.Equal(10_000m, entry.Spend.Crc);
        Assert.Null(entry.Income);
    }

    [Fact]
    public void EmptyMonth_IsZeroSpend_NotAbsent()
    {
        var june = M(2026, 6, new DateOnly(2026, 5, 28));

        var entry = Assert.Single(MonthTrendCalculator.Calculate([june], [], [], 500m));

        Assert.Equal(MoneyPair.Zero, entry.Spend);
        Assert.Equal(MoneyPair.Zero, entry.Income);
    }
}
