using Vuelto.Core.Budget;

namespace Vuelto.Core.Tests.Budget;

/// <summary>CARDS-1: what identifies a card in a voucher — the brand label and the last four digits of the masked number each bank prints.</summary>
public class CardIdentityTests
{
    [Theory]
    [InlineData("VISA", "************1234", "VISA", "1234")]            // BAC
    [InlineData("MASTERCARD", "************0000", "MASTERCARD", "0000")] // BN voucher
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

    // ---- #210: a masked number whose digits are not the last four ----

    [Theory]
    [InlineData("XXXXXXXXXXX8755X", "XXXXXXXXXXX8755X")]   // BN pagos: the owner's Black card, which ends 7558
    [InlineData("xxxx xxxx xxx8 755x", "XXXXXXXXXXX8755X")] // spacing and case don't make it a different card
    [InlineData("****-****-***8-755*", "XXXXXXXXXXX8755X")]  // any mask character reads as a mask
    [InlineData("XXXXXXXXXXX0000X", "XXXXXXXXXXX0000X")]
    [InlineData("XXXXXXXXXXXXX55X", "XXXXXXXXXXXXX55X")]    // fewer than four visible digits: still a pattern, still not a last four
    public void Read_DigitsFollowedByAMask_AreAPattern_NotALastFour(string number, string pattern)
    {
        var read = CardIdentity.Read(number);

        Assert.Null(read.Last4);
        Assert.Equal(pattern, read.Pattern);
        Assert.True(read.Ambiguous);
        Assert.Null(CardIdentity.Parse(null, number)); // never auto-matched or auto-created by a guessed last four
        Assert.Null(CardIdentity.Last4(number));
    }

    [Theory]
    [InlineData("************7558", "7558")]
    [InlineData("****7558", "7558")]
    [InlineData("XXXX-XXXX-XXXX-7558", "7558")]
    [InlineData("3782 822463 10005", "0005")]
    public void Read_DigitsAtTheEnd_AreTheLastFour(string number, string last4)
    {
        var read = CardIdentity.Read(number);

        Assert.Equal((last4, (string?)null, false), (read.Last4, read.Pattern, read.Ambiguous));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("****")]
    public void Read_NoDigits_IsNothing(string? number)
    {
        Assert.Equal(new CardNumberReading(null, null), CardIdentity.Read(number));
    }

    [Theory]
    [InlineData("VISA", "************1234", "VISA ····1234")]
    [InlineData(null, "XXXXXXXXXXX8755X", "XXXXXXXXXXX8755X")]
    [InlineData("mastercard", "XXXXXXXXXXX8755X", "MASTERCARD XXXXXXXXXXX8755X")]
    [InlineData("VISA", null, null)]
    public void Label_IsWhatTheReviewQueueShows(string? brand, string? number, string? label)
    {
        Assert.Equal(label, CardIdentity.Label(brand, number));
    }

    [Fact]
    public void AutoName_IsBrandDashLastFour()
    {
        Assert.Equal("VISA-1234", CardIdentity.AutoName("VISA", "1234"));
        Assert.Equal("CARD-0000", CardIdentity.AutoName(CardIdentity.NormalizeBrand(null), "0000"));
    }
}
