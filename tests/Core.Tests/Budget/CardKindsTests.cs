using Vuelto.Core.Budget;

namespace Vuelto.Core.Tests.Budget;

/// <summary>CARDS-3: credit or debit is a property of the plastic, and it decides how the money leaves.</summary>
public class CardKindsTests
{
    [Theory]
    [InlineData(null, "credit")]        // every card that existed before this slice
    [InlineData("", "credit")]
    [InlineData("  DEBIT ", "debit")]
    [InlineData("Credit", "credit")]
    public void Normalize_DefaultsToCredit_AndIsCaseInsensitive(string? input, string expected) =>
        Assert.Equal(expected, CardKinds.Normalize(input));

    [Fact]
    public void Normalize_RefusesAnythingElse() => Assert.Null(CardKinds.Normalize("prepaid"));

    [Theory]
    [InlineData("debit", "bank_account")] // a debit card spends the account, not a credit line
    [InlineData("credit", "credit_card")]
    [InlineData(null, "credit_card")]
    public void PaymentMethod_FollowsTheKind(string? kind, string expected) =>
        Assert.Equal(expected, CardKinds.PaymentMethod(kind));
}
