using HtmlAgilityPack;
using Vuelto.Core.Vouchers;

namespace Vuelto.Infrastructure.Vouchers;

/// <summary>HtmlAgilityPack helpers shared by the voucher extractors (EMAIL-1). Kept in Infrastructure so HAP stays out of the pure Core layer.</summary>
internal static class HtmlVouchers
{
    public static HtmlDocument Load(string? html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html ?? string.Empty);
        return doc;
    }

    /// <summary>Trimmed, entity-decoded text of a node.</summary>
    public static string Text(HtmlNode? node) => node is null ? string.Empty : HtmlEntity.DeEntitize(node.InnerText).Trim();

    /// <summary>All table rows as their trimmed cell texts (td/th), structure-agnostic.</summary>
    public static IEnumerable<string[]> Rows(HtmlDocument doc)
    {
        var rows = doc.DocumentNode.SelectNodes("//tr");
        if (rows is null) yield break;
        foreach (var row in rows)
        {
            var cells = row.SelectNodes("./td|./th");
            if (cells is null || cells.Count == 0) continue;
            yield return cells.Select(Text).ToArray();
        }
    }

    /// <summary>Two-or-more-cell rows as (normalized label, raw value) — the common label/value voucher layout.</summary>
    public static IEnumerable<(string Label, string Value)> LabelValueRows(string? html)
    {
        foreach (var cells in Rows(Load(html)))
            if (cells.Length >= 2)
                yield return (VoucherText.NormalizeLabel(cells[0]), cells[1]);
    }

    public static string? SelectText(HtmlDocument doc, string xpath) => doc.DocumentNode.SelectSingleNode(xpath) is { } n ? Text(n) : null;
}
