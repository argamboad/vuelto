namespace Vuelto.Api.Features.Reports.Pdf;

/// <summary>
/// REPORTS-9: the transactions appendix's optional columns, in print order. Date and payee always print (a row means
/// nothing without them) and are accepted in a request but never dropped. <c>amount</c> is the "show in" side(s).
/// </summary>
public static class ReportPdfColumns
{
    public const string Date = "date", Payee = "payee";
    public const string Category = "category", Class = "class", Amount = "amount", Rate = "rate", Method = "method",
        Bank = "bank", Source = "source", Card = "card", Notes = "notes";

    /// <summary>Every column that can be left out, in print order.</summary>
    public static readonly IReadOnlyList<string> Optional = [Category, Class, Amount, Rate, Method, Bank, Source, Card, Notes];

    private static readonly HashSet<string> Always = [Date, Payee];

    /// <summary>
    /// The requested set, normalized (trimmed, lower-cased, in print order): <c>null</c> when every optional column is
    /// asked for (or the list is absent) — the full appendix. <paramref name="unknown"/> names the first key that is not
    /// a column.
    /// </summary>
    public static IReadOnlyList<string>? Normalize(IEnumerable<string>? requested, out string? unknown)
    {
        unknown = null;
        if (requested is null) return null;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in requested)
        {
            var key = (raw ?? "").Trim().ToLowerInvariant();
            if (Always.Contains(key)) continue;
            if (!Optional.Contains(key)) { unknown = raw ?? ""; return null; }
            keys.Add(key);
        }
        return keys.Count == Optional.Count ? null : Optional.Where(keys.Contains).ToList();
    }
}
