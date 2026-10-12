namespace Vuelto.Core.Budget;

/// <summary>The three ways amounts can be shown (DISPLAY-1): one side of the pair, or both.</summary>
public static class DisplayCurrencies
{
    public const string Crc = Currencies.Crc;
    public const string Usd = Currencies.Usd;
    public const string Both = "both";
    public static readonly IReadOnlyList<string> All = [Crc, Usd, Both];

    /// <summary>Case-insensitive; null when the value is not one of the three.</summary>
    public static string? Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "crc" => Crc,
        "usd" => Usd,
        "both" => Both,
        _ => null,
    };
}
