namespace Vuelto.Core.Text;

/// <summary>
/// Cuts a string to a column's length without splitting a surrogate pair (v4 T38, R96). A plain
/// <c>s[..max]</c> can end on the high half of an emoji, and a string holding a lone surrogate is not valid
/// Unicode: Postgres refuses the write, so an error message ending in half an emoji lost the very bookkeeping
/// row that was recording the error. Every column-bound text write goes through here.
/// </summary>
public static class SafeTruncation
{
    public static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value ?? string.Empty;
        if (maxLength <= 0)
            return string.Empty;
        // Never end on a high surrogate: its low half would be the next unit, which the cut drops.
        var cut = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return value[..cut];
    }
}
