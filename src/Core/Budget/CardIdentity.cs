using System.Text.RegularExpressions;

namespace Vuelto.Core.Budget;

/// <summary>
/// The two ways a masked card number can read (#210, ADR-V027): its <see cref="Last4"/> when the digits close the
/// number (BAC <c>************1234</c>), or a <see cref="Pattern"/> when a mask follows them (BN payments print
/// <c>XXXXXXXXXXX8755X</c> for a card that ends in 7558 — the visible digits are not the last four, and guessing they
/// were booked payments on the wrong card). At most one is set; both are null when the text has no digits.
/// </summary>
public sealed record CardNumberReading(string? Last4, string? Pattern)
{
    public bool Ambiguous => Pattern is not null;
}

/// <summary>
/// What identifies a card across vouchers (CARDS-1): the brand the voucher labels it with and the last four
/// digits of the masked number it prints (BAC <c>************1234</c>). A number whose digits are followed by a
/// mask (BN <c>XXXXXXXXXXX8755X</c>) has no knowable last four: it is read as a pattern, which the household maps
/// to a card once and the app remembers (#210, ADR-V027). Pure — the one rule the staging, the confirm and the
/// review queue share.
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

    /// <summary>
    /// Reads a printed number (#210): spaces and dashes are layout; any other non-digit is a mask. Digits that close
    /// the number give its last four (from a run of at least four); digits followed by a mask give a pattern instead —
    /// the number upper-cased with every mask character as <c>X</c>, so the same card always prints the same pattern.
    /// </summary>
    public static CardNumberReading Read(string? cardNumber)
    {
        var compact = Layout().Replace(cardNumber ?? "", "");
        var lastDigit = -1;
        for (var i = compact.Length - 1; i >= 0; i--)
            if (char.IsAsciiDigit(compact[i])) { lastDigit = i; break; }
        if (lastDigit < 0) return new(null, null);

        if (lastDigit < compact.Length - 1)
            return new(null, string.Concat(compact.Select(c => char.IsAsciiDigit(c) ? c : 'X')));

        var runs = DigitRun().Matches(compact);
        return runs.Count == 0 ? new(null, null) : new(runs[^1].Value[^4..], null);
    }

    /// <summary>The last four digits when the number ends in them; null when it doesn't (a pattern) or has none.</summary>
    public static string? Last4(string? cardNumber) => Read(cardNumber).Last4;

    /// <summary>
    /// What the review queue shows for the card a voucher printed: <c>VISA ····1234</c> for a last four, the pattern
    /// itself (after the brand, when one is named) when the digits aren't the last four, null for no card.
    /// </summary>
    public static string? Label(string? brand, string? cardNumber)
    {
        var read = Read(cardNumber);
        var named = string.IsNullOrWhiteSpace(brand) ? null : NormalizeBrand(brand);
        if (read.Last4 is { } last4) return $"{named ?? UnknownBrand} ····{last4}";
        if (read.Pattern is { } pattern) return named is null ? pattern : $"{named} {pattern}";
        return null;
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

    [GeneratedRegex(@"[\s\-]+")]
    private static partial Regex Layout();
}
