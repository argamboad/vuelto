using Vuelto.Core.Budget;

namespace Vuelto.Core.Tests.Budget;

/// <summary>ADR-V007 vocabulary: the five classes (three of them expenses), the two payment methods (credit card default), the sources.</summary>
public class TransactionVocabularyTests
{
    [Theory]
    [InlineData("budgeted", "budgeted")]
    [InlineData(" Extraordinary ", "extraordinary")]
    [InlineData("UNPLANNED_ESSENTIAL", "unplanned_essential")]
    [InlineData("inflow", "inflow")]
    [InlineData("envelope_contribution", "envelope_contribution")]
    public void TransactionTypes_Normalize_AcceptsTheFiveClasses(string input, string expected) =>
        Assert.Equal(expected, TransactionTypes.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("incidental")] // the donor's pre-rename class is gone
    public void TransactionTypes_Normalize_RejectsUnknown(string? input) => Assert.Null(TransactionTypes.Normalize(input));

    [Fact]
    public void ExactlyThreeClasses_AreExpenses()
    {
        Assert.Equal(5, TransactionTypes.All.Count);
        Assert.Equal(new[] { "budgeted", "extraordinary", "unplanned_essential" }, TransactionTypes.Expenses.OrderBy(x => x));
    }

    [Theory]
    [InlineData(null, "credit_card")]
    [InlineData("", "credit_card")]
    [InlineData("Bank_Account", "bank_account")]
    public void PaymentMethods_Normalize_DefaultsToCreditCard(string? input, string expected) =>
        Assert.Equal(expected, PaymentMethods.Normalize(input));

    [Fact]
    public void PaymentMethods_Normalize_RejectsUnknown() => Assert.Null(PaymentMethods.Normalize("cash"));

    [Theory]
    [InlineData("manual", true)]
    [InlineData("email", false)]
    [InlineData("refund_realization", false)]
    public void Sources_OnlyManualIsEditable(string source, bool editable) => Assert.Equal(editable, TransactionSources.IsEditable(source));
}
