using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Vuelto.Core.Vouchers;

/// <summary>
/// Tolerant text helpers for voucher parsing — what makes the parser resilient to the cosmetic
/// changes (accents, colons, casing, separators) that break naive extractors.
/// </summary>
public static partial class VoucherText
{
    /// <summary>Card-brand labels that appear as a row label with the masked number as the value.</summary>
    public static readonly IReadOnlySet<string> CardBrands =
        new HashSet<string>(StringComparer.Ordinal) { "VISA", "MASTERCARD", "AMEX", "AMERICAN EXPRESS" };

    public static bool IsCardBrand(string normalizedLabel) => CardBrands.Contains(normalizedLabel);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\d[\d.,]*")]
    private static partial Regex Number();

    /// <summary>
    /// Normalizes a label for matching: trims, drops ':' and '.', strips accents, collapses whitespace,
    /// uppercases — so "Comercio:", " comercio ", "COMERCIO" and "Nro. Aut:" / "NRO AUT" all compare equal.
    /// </summary>
    public static string NormalizeLabel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = StripDiacritics(raw).Replace(":", " ").Replace(".", " ");
        return Whitespace().Replace(s, " ").Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Parses "CRC 52000.00", "₡52,000.00", "$ 1,234.50", "COLONES 5000", "5000 CRC" into a currency
    /// ("CRC"/"USD", or null if absent) and an amount. False only when no numeric amount is present.
    /// </summary>
    public static bool TryParseMoney(string? raw, out string? currency, out decimal amount)
    {
        currency = null;
        amount = 0m;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var upper = raw.ToUpperInvariant();
        if (raw.Contains('₡') || upper.Contains("CRC") || upper.Contains("COLON"))
            currency = "CRC";
        else if (raw.Contains('$') || upper.Contains("USD") || upper.Contains("DOLAR") || upper.Contains("DOLLAR"))
            currency = "USD";

        var match = Number().Match(raw);
        return match.Success && TryParseDecimal(match.Value, out amount);
    }

    /// <summary>Maps the Spanish currency word/code on a voucher to a code, else null.</summary>
    public static string? NormalizeCurrency(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var u = raw.Trim().ToUpperInvariant();
        if (u is "CRC" || u.Contains("COLON") || raw.Contains('₡')) return "CRC";
        if (u is "USD" || u.Contains("DOLAR") || u.Contains("DOLLAR") || raw.Contains('$')) return "USD";
        return null;
    }

    private static bool TryParseDecimal(string numeric, out decimal amount)
    {
        amount = 0m;
        var hasDot = numeric.Contains('.');
        var hasComma = numeric.Contains(',');
        string normalized;

        if (hasDot && hasComma)
        {
            // The later separator is the decimal point; the earlier is thousands.
            var dec = numeric.LastIndexOf('.') > numeric.LastIndexOf(',') ? '.' : ',';
            var thou = dec == '.' ? ',' : '.';
            normalized = numeric.Replace(thou.ToString(), "").Replace(dec, '.');
        }
        else if (hasComma)
        {
            // A lone comma is a decimal only when it looks like one (1–2 trailing digits).
            var trailing = numeric.Length - numeric.LastIndexOf(',') - 1;
            normalized = trailing is 1 or 2 ? numeric.Replace(',', '.') : numeric.Replace(",", "");
        }
        else if (hasDot)
        {
            // WU-4 A8: a lone dot is a decimal only with 1–2 trailing digits; exactly 3 is a thousands
            // separator (₡1.500 = 1500, not 1.5), so strip it.
            var trailing = numeric.Length - numeric.LastIndexOf('.') - 1;
            normalized = trailing is 1 or 2 ? numeric : numeric.Replace(".", "");
        }
        else
        {
            normalized = numeric;
        }

        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }

    private static string StripDiacritics(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
