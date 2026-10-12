using System.Globalization;
using System.Text.RegularExpressions;

namespace Vuelto.Core.Vouchers;

/// <summary>
/// Parses the Spanish-language dates Costa Rican bank vouchers use ("12 Ene 2026",
/// "Jun 13, 2026, 14:01", "12 Ene, 2026 - 14:30", "Sep 1, 2026 - 10:13 a.m.", "16/06/2026 14:30:00").
/// Month matching is case-insensitive and accepts the CR "set" variant for September; numeric dates are
/// always read day-first. Returns the calendar date only, and the time is <b>cut off before parsing</b>
/// rather than handed to <c>DateTime.TryParse</c>: BN prints a 12-hour clock with the Spanish dotted
/// meridiem ("10:13 a.m."), which no culture reads, and it took the whole date down with it (2026-09-10).
/// </summary>
public static partial class SpanishDateParser
{
    private static readonly Dictionary<string, string> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ene"] = "Jan", ["feb"] = "Feb", ["mar"] = "Mar", ["abr"] = "Apr",
        ["may"] = "May", ["jun"] = "Jun", ["jul"] = "Jul", ["ago"] = "Aug",
        ["sep"] = "Sep", ["set"] = "Sep", ["oct"] = "Oct", ["nov"] = "Nov", ["dic"] = "Dec",
    };

    private static readonly string[] NumericFormats = ["d/M/yyyy", "d/M/yy", "d-M-yyyy", "d-M-yy"];

    [GeneratedRegex(@"\b(\d{1,2})[/-](\d{1,2})[/-](\d{2,4})\b")]
    private static partial Regex NumericDate();

    /// <summary>A clock time with an optional meridiem in any spelling — "14:01", "10:13 a.m.", "10:13 a. m.", "10:13AM".</summary>
    [GeneratedRegex(@"\b\d{1,2}:\d{2}(?::\d{2})?\s*(?:[ap]\.?\s?m\.?)?", RegexOptions.IgnoreCase)]
    private static partial Regex TimeOfDay();

    public static DateOnly? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        // Drop the time first: we keep the calendar date only, and a token the culture cannot read
        // ("10:13 a.m.") otherwise fails the WHOLE string. The colon makes the match unambiguous —
        // no date part can look like a time. Then shed the separator it hung from ("2026 - " → "2026").
        var s = TimeOfDay().Replace(value, "").Trim().TrimEnd(',', '-', ' ');
        if (s.Length == 0) return null;

        // Numeric day-first dates (BN payment). CR vouchers are day-first, so parse the token with
        // explicit d/M formats — never let an ambiguous "06/07/2026" be read month-first.
        var numeric = NumericDate().Match(s);
        if (numeric.Success)
        {
            var token = $"{numeric.Groups[1].Value}/{numeric.Groups[2].Value}/{numeric.Groups[3].Value}";
            if (DateTime.TryParseExact(token, NumericFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var nd))
                return DateOnly.FromDateTime(nd);
        }

        // Month-name dates (BAC / BN voucher), e.g. "12 Ene 2026", "12 Ene, 2026 - 14:30".
        var normalized = s.Replace(" -", ",");
        foreach (var (es, en) in Months)
            normalized = Regex.Replace(normalized, $@"\b{es}\b", en, RegexOptions.IgnoreCase);

        if (DateTime.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            || DateTime.TryParse(normalized, CultureInfo.CurrentCulture, DateTimeStyles.None, out dt))
            return DateOnly.FromDateTime(dt);
        return null;
    }
}
