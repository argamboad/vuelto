namespace Vuelto.Core.Budget;

/// <summary>One slice of the month's income (INCOME-2): whose it is and how much, as a pair at the day's rate.</summary>
/// <param name="Kind">One of <see cref="IncomeByMember.Member"/>, <see cref="IncomeByMember.Household"/>,
/// <see cref="IncomeByMember.FormerMember"/>, <see cref="IncomeByMember.Inflows"/>.</param>
/// <param name="MemberUserId">Set for <see cref="IncomeByMember.Member"/> only.</param>
/// <param name="Name">The member's display name (or email) for <see cref="IncomeByMember.Member"/>; null otherwise — the
/// client labels the other kinds in its own language.</param>
public record IncomeMemberSlice(string Kind, Guid? MemberUserId, string? Name, MoneyPair Amount);

/// <summary>
/// INCOME-2: the month's income cut by whose it is. Rows of a current member are summed under that member; rows with
/// no member are "the household"; rows whose member is no longer in the household are one "former member" bucket
/// (their names are not ours to keep); inflow transactions are their own bucket. The buckets add up to
/// <see cref="IncomeSummary.Total"/> (both are sums of the same 2-dp pairs). Pure, no I/O.
/// </summary>
public static class IncomeByMember
{
    public const string Member = "member", Household = "household", FormerMember = "former_member", Inflows = "inflows";

    /// <param name="memberNames">The household's current members: user id → display name.</param>
    public static IReadOnlyList<IncomeMemberSlice> Group(IncomeSummary income, IReadOnlyDictionary<Guid, string> memberNames)
    {
        var members = income.Rows
            .Where(r => r.MemberUserId is { } id && memberNames.ContainsKey(id))
            .GroupBy(r => r.MemberUserId!.Value)
            .Select(g => new IncomeMemberSlice(Member, g.Key, memberNames[g.Key], Sum(g)))
            .OrderByDescending(s => s.Amount.Crc)
            .ThenBy(s => s.Name, StringComparer.CurrentCulture)
            .ToList();

        var slices = new List<IncomeMemberSlice>(members)
        {
            new(Household, null, null, Sum(income.Rows.Where(r => r.MemberUserId is null))),
            new(FormerMember, null, null, Sum(income.Rows.Where(r => r.MemberUserId is { } id && !memberNames.ContainsKey(id)))),
            new(Inflows, null, null, income.Inflows),
        };
        return slices.Where(s => s.Amount.Crc != 0m || s.Amount.Usd != 0m).ToList();
    }

    private static MoneyPair Sum(IEnumerable<IncomeRowSummary> rows)
    {
        var list = rows.ToList();
        return new MoneyPair(list.Sum(r => r.Pair.Crc), list.Sum(r => r.Pair.Usd));
    }
}
