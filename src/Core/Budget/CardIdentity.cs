using System.Text.RegularExpressions;

namespace Vuelto.Core.Budget;

/// <summary>
/// What identifies a card across vouchers (CARDS-1): the brand the voucher labels it with and the last four
/// digits of the masked number it prints — BAC <c>************1234</c>, BN <c>XXXXXXXXXXX1234X</c>. Pure.
/// </summary>
public static partial class CardIdentity
{
    /// <summary>The brand used when a voucher prints a card number without naming the brand (BN payment receipts).</summary>
    public const string UnknownBrand = "CARD";
    public static readonly IReadOnlyList<string> KnownBrands = ["VISA", "MASTERCARD", "AMEX", UnknownBrand];

    public static string NormalizeBrand(string? brand)
    {
        var b = Whitespace().Replace(brand?.Trim().ToUpperInvariant() ?? "", " ");
        return b switch
        {
            "" => UnknownBrand,
            "AMERICAN EXPRESS" => "AMEX",
            "MASTER CARD" => "MASTERCARD",
            _ => b,
        };
    }

    /// <summary>The last four digits of the last run of four or more digits; null when the text has none.</summary>
    public static string? Last4(string? cardNumber)
    {
        if (string.IsNullOrWhiteSpace(cardNumber)) return null;
        var runs = DigitRun().Matches(cardNumber);
        return runs.Count == 0 ? null : runs[^1].Value[^4..];
    }

    /// <summary>The automatic alias a card gets on first sight — <c>VISA-1234</c>.</summary>
    public static string AutoName(string brand, string last4) => $"{brand}-{last4}";

    /// <summary>Brand + last four when the text carries a card number; null when it does not.</summary>
    public static (string Brand, string Last4)? Parse(string? brand, string? cardNumber) =>
        Last4(cardNumber) is { } last4 ? (NormalizeBrand(brand), last4) : null;

    [GeneratedRegex(@"\d{4,}")]
    private static partial Regex DigitRun();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
