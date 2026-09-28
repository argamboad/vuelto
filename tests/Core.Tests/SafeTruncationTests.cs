using Vuelto.Core.Text;

namespace Vuelto.Core.Tests;

/// <summary>v4 T38 (R96): truncation never leaves half an emoji behind.</summary>
public class SafeTruncationTests
{
    [Fact]
    public void Truncate_IsRuneSafe_AtASurrogateBoundary()
    {
        var text = new string('x', 999) + "😀tail"; // the emoji is two UTF-16 units, at 999..1000

        var cut = SafeTruncation.Truncate(text, 1000);

        Assert.Equal(999, cut.Length); // the whole emoji goes, not half of it
        Assert.DoesNotContain(cut, c => char.IsSurrogate(c));
    }

    [Theory]
    [InlineData("", 5, "")]
    [InlineData(null, 5, "")]
    [InlineData("abc", 5, "abc")]
    [InlineData("abcdef", 3, "abc")]
    [InlineData("ab😀", 4, "ab😀")]   // fits exactly
    [InlineData("ab😀c", 3, "ab")]    // would split the pair
    [InlineData("abc", 0, "")]
    public void Truncate_KeepsWhatFits(string? value, int max, string expected)
    {
        Assert.Equal(expected, SafeTruncation.Truncate(value, max));
    }
}
