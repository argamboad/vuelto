using System.Text;
using HtmlAgilityPack;
using Vuelto.Core.Vouchers;

namespace Vuelto.Infrastructure.Vouchers;

/// <summary>
/// Banco Nacional utility-payment notice ("BN Conectividad le informa"). Not a label/value table —
/// labels are styled <c>&lt;font&gt;</c> nodes and the value is the text node that follows. The paid
/// service name is the FIRST bold heading's first line (never the line-items table header further
/// down). Never throws.
/// </summary>
public sealed class BnPaymentExtractor : IBankVoucherExtractor
{
    public string Key => VoucherSources.BnPayment;
    public VoucherBank Bank => VoucherBank.BN;

    public ParsedVoucher Extract(string htmlBody)
    {
        try
        {
            var doc = HtmlVouchers.Load(htmlBody);
            string? currency = null, card = null, auth = null, reference = null;
            decimal? amount = null;
            DateOnly? date = null;

            var merchant = FirstHeadingLine(doc);

            var labelNodes = doc.DocumentNode.SelectNodes("//b/font[@color='#102356']");
            if (labelNodes is not null)
            {
                foreach (var labelNode in labelNodes)
                {
                    var label = VoucherText.NormalizeLabel(labelNode.InnerText);
                    var value = ValueAfterLabel(labelNode);
                    switch (label)
                    {
                        case "NO COMPROBANTE DEBITO": reference = value; auth = value; break;
                        case "MONEDA": currency = VoucherText.NormalizeCurrency(value); break;
                        case "MONTO":
                            if (VoucherText.TryParseMoney(value, out var cur, out var amt)) { amount = amt; currency ??= cur; }
                            break;
                        case "TARJETA DE CREDITO": card = value; break;
                        case "FECHA Y HORA DEL PAGO": date = SpanishDateParser.TryParse(value); break;
                    }
                }
            }

            return new ParsedVoucher
            {
                Bank = Bank, Merchant = merchant, Amount = amount, Currency = currency, Date = date,
                CardNumber = card, Authorization = auth, Reference = reference, TransactionType = "PAGO"
            };
        }
        catch
        {
            return new ParsedVoucher { Bank = Bank, TransactionType = "PAGO" };
        }
    }

    /// <summary>The first line of the first bold coloured heading — stops at the first <c>&lt;br&gt;</c> so the trailing label doesn't bleed in.</summary>
    private static string? FirstHeadingLine(HtmlDocument doc)
    {
        var node = doc.DocumentNode.SelectSingleNode("//b/font[@color='#102356']")
            ?? doc.DocumentNode.SelectSingleNode("//font[@color='#102356']");
        if (node is null) return null;

        var sb = new StringBuilder();
        foreach (var child in node.ChildNodes)
        {
            if (string.Equals(child.Name, "br", StringComparison.OrdinalIgnoreCase)) break;
            if (child.NodeType == HtmlNodeType.Text) sb.Append(child.InnerText);
        }
        var text = HtmlEntity.DeEntitize(sb.ToString()).Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>The first non-empty text node following the label's bold container.</summary>
    private static string ValueAfterLabel(HtmlNode labelNode)
    {
        var node = labelNode.ParentNode?.NextSibling;
        while (node is not null)
        {
            if (node.NodeType == HtmlNodeType.Text)
            {
                var text = HtmlEntity.DeEntitize(node.InnerText).Trim();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            node = node.NextSibling;
        }
        return string.Empty;
    }
}
