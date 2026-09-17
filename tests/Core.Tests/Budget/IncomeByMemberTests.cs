using Vuelto.Core.Budget;

namespace Vuelto.Core.Tests.Budget;

/// <summary>
/// INCOME-2: the month's income cut by whose it is — one bucket per current member (named), one for the household
/// (rows with no member), one for rows whose member has left, one for inflows. The buckets add up to the income total;
/// empty buckets are left out; members come first by size, then household, former members, inflows.
/// </summary>
public class IncomeByMemberTests
{
    private static readonly Guid Allan = Guid.CreateVersion7(), Maria = Guid.CreateVersion7(), Gone = Guid.CreateVersion7();

    private static IncomeRowSummary Row(Guid? member, decimal crc, decimal usd) =>
        new(Guid.CreateVersion7(), "x", member, "CRC", crc, null, new MoneyPair(crc, usd));

    private static readonly Dictionary<Guid, string> Names = new() { [Allan] = "Allan", [Maria] = "María" };

    [Fact]
    public void GroupsRowsByMember_AndTheBucketsAddUpToTheTotal()
    {
        var summary = new IncomeSummary(
            [Row(Allan, 500_000m, 1_000m), Row(Maria, 600_000m, 1_200m), Row(null, 100_000m, 200m), Row(Allan, 700_000m, 1_400m), Row(Gone, 50_000m, 100m)],
            new MoneyPair(25_000m, 50m), new MoneyPair(1_975_000m, 3_950m));

        var buckets = IncomeByMember.Group(summary, Names);

        Assert.Equal(
            [
                (IncomeByMember.Member, (Guid?)Allan, (string?)"Allan", 1_200_000m, 2_400m),
                (IncomeByMember.Member, Maria, "María", 600_000m, 1_200m),
                (IncomeByMember.Household, null, null, 100_000m, 200m),
                (IncomeByMember.FormerMember, null, null, 50_000m, 100m),
                (IncomeByMember.Inflows, null, null, 25_000m, 50m),
            ],
            buckets.Select(b => (b.Kind, b.MemberUserId, b.Name, b.Amount.Crc, b.Amount.Usd)));
        Assert.Equal((summary.Total.Crc, summary.Total.Usd), (buckets.Sum(b => b.Amount.Crc), buckets.Sum(b => b.Amount.Usd)));
    }

    [Fact]
    public void EmptyBuckets_AreLeftOut()
    {
        var summary = new IncomeSummary([Row(Allan, 500_000m, 1_000m), Row(Maria, 0m, 0m)], MoneyPair.Zero, new MoneyPair(500_000m, 1_000m));

        var bucket = Assert.Single(IncomeByMember.Group(summary, Names));

        Assert.Equal(Allan, bucket.MemberUserId);
    }

    [Fact]
    public void NoIncome_IsAnEmptyList()
    {
        Assert.Empty(IncomeByMember.Group(new IncomeSummary([], MoneyPair.Zero, MoneyPair.Zero), Names));
    }

    [Fact]
    public void EqualMembers_AreOrderedByName()
    {
        var summary = new IncomeSummary([Row(Maria, 100m, 1m), Row(Allan, 100m, 1m)], MoneyPair.Zero, new MoneyPair(200m, 2m));

        Assert.Equal(["Allan", "María"], IncomeByMember.Group(summary, Names).Select(b => b.Name));
    }
}
