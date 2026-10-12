namespace Vuelto.Core.Budget;

/// <summary>
/// The two currencies every amount in the app is stored and displayed in (ADR-V004). Stored values
/// are the ISO codes, upper-case.
/// </summary>
public static class Currencies
{
    public const string Crc = "CRC";
    public const string Usd = "USD";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Crc, Usd };

    /// <summary>Normalizes user input to a stored value, or null when it is not a supported currency.</summary>
    public static string? Normalize(string? value)
    {
        var upper = value?.Trim().ToUpperInvariant();
        return upper is not null && All.Contains(upper) ? upper : null;
    }
}
