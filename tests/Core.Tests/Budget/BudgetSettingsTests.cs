using Vuelto.Core.Budget;
using Vuelto.Core.Entities;

namespace Vuelto.Core.Tests.Budget;

/// <summary>The defaults a household runs on before it saves anything (BUDGET-1), plus the stored-value sets.</summary>
public class BudgetSettingsTests
{
    [Fact]
    public void Defaults_MatchTheDonor_ThursdayLastWeekdayPrevUsd()
    {
        var tenant = Guid.CreateVersion7();
        var s = BudgetSettings.Defaults(tenant);

        Assert.Equal(tenant, s.TenantId);
        Assert.Equal(4, s.WeekStartWeekday);
        Assert.Equal(MonthAnchors.LastWeekdayPrev, s.MonthAnchor);
        Assert.Equal(0m, s.PrimaryIncome4w);
        Assert.Equal(0m, s.SecondaryIncome5w);
        Assert.Equal(Currencies.Usd, s.PrimaryIncomeCurrency);
        Assert.Equal(Currencies.Usd, s.SecondaryIncomeCurrency);
    }

    [Theory]
    [InlineData("usd", "USD")]
    [InlineData(" crc ", "CRC")]
    [InlineData("EUR", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Currencies_Normalize_AcceptsOnlyCrcAndUsd(string? input, string? expected) =>
        Assert.Equal(expected, Currencies.Normalize(input));

    [Fact]
    public void MonthAnchors_All_HoldsExactlyTheThreeStoredValues() =>
        Assert.Equal(3, MonthAnchors.All.Count);
}
