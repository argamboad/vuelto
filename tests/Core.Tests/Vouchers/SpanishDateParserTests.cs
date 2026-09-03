using Vuelto.Core.Vouchers;

namespace Vuelto.Core.Tests.Vouchers;

/// <summary>EMAIL-1 (donor US-025): Spanish month names incl. the CR "set", the banks' month-first shape, day-first numerics, and nulls for junk.</summary>
public class SpanishDateParserTests
{
    [Theory]
    [InlineData("12 Ene 2026", 2026, 1, 12)]
    [InlineData("12 Ene, 2026 - 14:30", 2026, 1, 12)]
    [InlineData("15 Feb 2026", 2026, 2, 15)]
    [InlineData("01 Mar 2026", 2026, 3, 1)]
    [InlineData("30 Abr 2026", 2026, 4, 30)]
    [InlineData("09 May 2026", 2026, 5, 9)]
    [InlineData("21 Jun 2026", 2026, 6, 21)]
    [InlineData("04 Jul 2026", 2026, 7, 4)]
    [InlineData("18 Ago 2026", 2026, 8, 18)]
    [InlineData("07 Sep 2026", 2026, 9, 7)]
    [InlineData("07 Set 2026", 2026, 9, 7)]
    [InlineData("13 Oct 2026", 2026, 10, 13)]
    [InlineData("25 Nov 2026", 2026, 11, 25)]
    [InlineData("31 Dic 2026", 2026, 12, 31)]
    [InlineData("12 ENE 2026", 2026, 1, 12)]
    public void Parses_SpanishMonthNameDates(string input, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), SpanishDateParser.TryParse(input));

    [Theory]
    [InlineData("Ene 13, 2026, 14:01", 2026, 1, 13)]   // BAC shape
    [InlineData("Abr 03, 2026, 09:30", 2026, 4, 3)]
    [InlineData("Ago 18, 2026, 23:05", 2026, 8, 18)]
    [InlineData("Dic 31, 2026, 00:01", 2026, 12, 31)]
    [InlineData("Ene 09, 2026 - 07:28", 2026, 1, 9)]   // BN voucher single-cell shape
    [InlineData("Set 09, 2026 - 07:28", 2026, 9, 9)]
    public void Parses_BankMonthFirstSpanishDates(string input, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), SpanishDateParser.TryParse(input));

    [Theory]
    [InlineData("16/06/2026", 2026, 6, 16)]
    [InlineData("16/06/2026 14:30:00", 2026, 6, 16)]
    [InlineData("06/07/2026", 2026, 7, 6)]             // day-first, NOT month-first
    [InlineData("6-7-2026", 2026, 7, 6)]
    public void Parses_NumericDayFirstDates(string input, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), SpanishDateParser.TryParse(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    [InlineData("32 Xyz 2026")]
    public void ReturnsNull_ForUnparseable(string? input) => Assert.Null(SpanishDateParser.TryParse(input));
}
