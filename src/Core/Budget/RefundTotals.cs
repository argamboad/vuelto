using Vuelto.Core.Entities;

namespace Vuelto.Core.Budget;

/// <summary>
/// #206: how much of what is expected back has come in, and how much is still out — computed from the refunds, never
/// stored (golden rule 4). Both currencies, 2 dp. <see cref="Expected"/> is the sum of the other two.
/// </summary>
public sealed record RefundTotalsResult(MoneyPair Pending, MoneyPair Received, MoneyPair Expected);

public static class RefundTotals
{
    public static RefundTotalsResult Calculate(IEnumerable<Refund> refunds)
    {
        decimal pCrc = 0, pUsd = 0, rCrc = 0, rUsd = 0;
        foreach (var r in refunds)
        {
            if (string.Equals(r.Status, RefundStatuses.Received, StringComparison.Ordinal)) { rCrc += r.AmountCrc; rUsd += r.AmountUsd; }
            else { pCrc += r.AmountCrc; pUsd += r.AmountUsd; }
        }
        return new RefundTotalsResult(Pair(pCrc, pUsd), Pair(rCrc, rUsd), Pair(pCrc + rCrc, pUsd + rUsd));
    }

    private static MoneyPair Pair(decimal crc, decimal usd) => new(CurrencyMath.Round2(crc), CurrencyMath.Round2(usd));
}
