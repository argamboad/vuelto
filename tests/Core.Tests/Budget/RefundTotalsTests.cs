using Vuelto.Core.Budget;
using Vuelto.Core.Entities;

namespace Vuelto.Core.Tests.Budget;

/// <summary>#206: received and pending refunds add up separately, in both currencies; expected is their sum.</summary>
public class RefundTotalsTests
{
    private static Refund R(decimal crc, decimal usd, string status) => new()
    {
        TenantId = Guid.NewGuid(), MonthId = Guid.NewGuid(), TransactionId = Guid.NewGuid(), Payee = "x",
        TransactionDate = new DateOnly(2026, 10, 1), AmountCrc = crc, AmountUsd = usd, Status = status,
    };

    [Fact]
    public void MixedStatuses_SplitIntoReceivedAndPending_AndExpectedIsTheirSum()
    {
        var totals = RefundTotals.Calculate(
        [
            R(15_000m, 30m, RefundStatuses.Pending),
            R(5_000.005m, 10.004m, RefundStatuses.Pending),
            R(6_170m, 12.34m, RefundStatuses.Received),
        ]);

        Assert.Equal(new MoneyPair(20_000.01m, 40m), totals.Pending);   // 2 dp, away from zero
        Assert.Equal(new MoneyPair(6_170m, 12.34m), totals.Received);
        Assert.Equal(new MoneyPair(26_170.01m, 52.34m), totals.Expected);
    }

    [Fact]
    public void NoRefunds_IsAllZeros()
    {
        var totals = RefundTotals.Calculate([]);
        Assert.Equal((new MoneyPair(0, 0), new MoneyPair(0, 0), new MoneyPair(0, 0)), (totals.Pending, totals.Received, totals.Expected));
    }
}
