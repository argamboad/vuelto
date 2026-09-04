using Vuelto.Core.Entities;
using Vuelto.Core.Vouchers;

namespace Vuelto.Core.Tests.Vouchers;

/// <summary>EMAIL-5 (donor US-029 AC2): case-insensitive contains, the longest (most specific) pattern wins, blank or unmatched merchants yield nothing; the suggestible classes normalize.</summary>
public class MerchantMatcherTests
{
    private static MerchantCategoryMapping Rule(string pattern, string? cls = null) =>
        new() { MerchantPattern = pattern, PatternKey = MerchantCategoryMapping.KeyFor(pattern), CategoryId = Guid.CreateVersion7(), SuggestedClass = cls };

    [Fact]
    public void Matches_CaseInsensitively_ByContains()
    {
        var rule = Rule("automercado");
        Assert.Same(rule, MerchantMatcher.Resolve([rule], "AUTOMERCADO ESCAZU"));
        Assert.Same(rule, MerchantMatcher.Resolve([rule], "Pago AutoMercado"));
        Assert.Null(MerchantMatcher.Resolve([rule], "AUTO MERCADO"));
    }

    [Fact]
    public void TheLongestMatchingPattern_Wins()
    {
        var general = Rule("AUTOMERCADO");
        var specific = Rule("AUTOMERCADO ESCAZU");
        var unrelated = Rule("WALMART");
        Assert.Same(specific, MerchantMatcher.Resolve([general, unrelated, specific], "AUTOMERCADO ESCAZU 123"));
        Assert.Same(general, MerchantMatcher.Resolve([general, unrelated, specific], "AUTOMERCADO HEREDIA"));
    }

    [Fact]
    public void Ties_ResolveByPatternText_Deterministically()
    {
        var b = Rule("TACO BELL");
        var a = Rule("BELL PLAZ");
        Assert.Same(a, MerchantMatcher.Resolve([b, a], "TACO BELL PLAZA"));
        Assert.Same(a, MerchantMatcher.Resolve([a, b], "TACO BELL PLAZA"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankMerchant_YieldsNothing(string? merchant) => Assert.Null(MerchantMatcher.Resolve([Rule("A")], merchant));

    [Fact]
    public void NoRules_OrABlankPattern_YieldsNothing()
    {
        Assert.Null(MerchantMatcher.Resolve([], "AUTOMERCADO"));
        Assert.Null(MerchantMatcher.Resolve([Rule("  ")], "AUTOMERCADO"));
    }

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("", true, null)]
    [InlineData("budgeted", true, "budgeted")]
    [InlineData(" Extraordinary ", true, "extraordinary")]
    [InlineData("unplanned_essential", true, "unplanned_essential")]
    [InlineData("inflow", false, null)]
    [InlineData("envelope_contribution", false, null)]
    [InlineData("groceries", false, null)]
    public void SuggestibleClasses_NormalizeTheThreeSpendingClasses_AndRejectTheRest(string? input, bool ok, string? expected)
    {
        Assert.Equal(ok, SuggestibleClasses.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void KeyFor_TrimsAndLowerCases() => Assert.Equal("taco bell", MerchantCategoryMapping.KeyFor("  Taco BELL "));
}
