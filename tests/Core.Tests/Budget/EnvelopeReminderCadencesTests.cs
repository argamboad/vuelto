using Vuelto.Core.Budget;

namespace Vuelto.Core.Tests.Budget;

/// <summary>ENV-1: the two reminder cadences (ADR-V007) and their input normalization.</summary>
public class EnvelopeReminderCadencesTests
{
    [Theory]
    [InlineData("monthly", "monthly")]
    [InlineData("Monthly", "monthly")]
    [InlineData("  FIVE_WEEK_MONTHS ", "five_week_months")]
    public void Normalize_AcceptsKnownCadences_CaseAndSpaceInsensitively(string input, string expected) =>
        Assert.Equal(expected, EnvelopeReminderCadences.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("whenever")]
    [InlineData("five-week-months")]
    public void Normalize_RejectsUnknownCadences(string? input) =>
        Assert.Null(EnvelopeReminderCadences.Normalize(input));

    [Fact]
    public void ExactlyTwoCadences_Exist()
    {
        Assert.Equal(2, EnvelopeReminderCadences.All.Count);
        Assert.Contains(EnvelopeReminderCadences.Monthly, EnvelopeReminderCadences.All);
        Assert.Contains(EnvelopeReminderCadences.FiveWeekMonths, EnvelopeReminderCadences.All);
    }
}
