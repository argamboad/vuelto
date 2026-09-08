using Vuelto.Core.Budget;

namespace Vuelto.Core.Tests.Budget;

/// <summary>CARDS-1: what identifies a card in a voucher — the brand label and the last four digits of the masked number each bank prints.</summary>
public class CardIdentityTests
{
    [Theory]
    [InlineData("VISA", "************1234", "VISA", "1234")]            // BAC
    [InlineData("MASTERCARD", "************0000", "MASTERCARD", "0000")] // BN voucher
    [InlineData(null, "XXXXXXXXXXX0000X", "CARD", "0000")]              // BN payment receipt — no brand label, a check letter after the digits
    [InlineData("American Express", "3782 822463 10005", "AMEX", "0005")]
    [InlineData(" master card ", "4321", "MASTERCARD", "4321")]
    public void Parse_ReadsBrandAndLastFour(string? brand, string number, string expectedBrand, string expectedLast4)
    {
        Assert.Equal((expectedBrand, expectedLast4), CardIdentity.Parse(brand, number));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("****")]
    [InlineData("VISA 12")]
    public void Parse_IsNull_WhenTheTextCarriesNoFourDigits(string? number)
    {
        Assert.Null(CardIdentity.Parse("VISA", number));
    }

    [Fact]
    public void AutoName_IsBrandDashLastFour()
    {
        Assert.Equal("VISA-1234", CardIdentity.AutoName("VISA", "1234"));
        Assert.Equal("CARD-0000", CardIdentity.AutoName(CardIdentity.NormalizeBrand(null), "0000"));
    }
}
