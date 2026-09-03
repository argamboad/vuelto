using Vuelto.Core.Vouchers;

namespace Vuelto.Infrastructure.Vouchers;

/// <summary>
/// Banco Nacional card-transaction voucher ("Voucher Digital"): commerce and transaction type come
/// from styled nodes (the only anchors BN gives them); money/date/auth/ref rows are read
/// structure-agnostically from the table — single-cell rows are tried as a date, label/value rows
/// matched on a normalized label. Never throws.
/// </summary>
public sealed class BnVoucherExtractor : IBankVoucherExtractor
{
    public string Key => VoucherSources.BnVoucher;
    public VoucherBank Bank => VoucherBank.BN;

    public ParsedVoucher Extract(string htmlBody)
    {
        try
        {
            var doc = HtmlVouchers.Load(htmlBody);
            string? currency = null, card = null, auth = null, reference = null, txType = null;
            decimal? amount = null;
            DateOnly? date = null;

            // Commerce: the green banner paragraph.
            var merchant = HtmlVouchers.SelectText(doc, "//p[contains(@style,'background-color:#b0bd20')]")
                ?? HtmlVouchers.SelectText(doc, "//p[contains(@style,'#b0bd20')]");

            // Transaction type: "...comprobante de COMPRA ..." → first word after the phrase.
            var compr = HtmlVouchers.SelectText(doc, "//p[contains(text(),'comprobante de')]")
                ?? HtmlVouchers.SelectText(doc, "//*[contains(text(),'comprobante de')]");
            if (!string.IsNullOrWhiteSpace(compr))
            {
                var after = compr.Split("comprobante de", StringSplitOptions.RemoveEmptyEntries);
                if (after.Length > 1)
                {
                    var token = after[1].Trim();
                    var space = token.IndexOf(' ');
                    txType = (space > 0 ? token[..space] : token).Trim().ToUpperInvariant();
                }
            }

            foreach (var cells in HtmlVouchers.Rows(doc))
            {
                if (cells.Length == 1)
                {
                    date ??= SpanishDateParser.TryParse(cells[0]);
                    continue;
                }

                var label = VoucherText.NormalizeLabel(cells[0]);
                var value = cells[1].Trim();
                switch (label)
                {
                    case "NRO AUT": auth = value; break;
                    case "REF": reference = value; break;
                    case "TOTAL":
                        if (VoucherText.TryParseMoney(value, out var cur, out var amt)) { currency = cur; amount = amt; }
                        break;
                    default:
                        if (VoucherText.IsCardBrand(label)) card = value;
                        break;
                }
            }

            return new ParsedVoucher
            {
                Bank = Bank, Merchant = merchant, Amount = amount, Currency = currency, Date = date,
                CardNumber = card, Authorization = auth, Reference = reference, TransactionType = txType
            };
        }
        catch
        {
            return new ParsedVoucher { Bank = Bank };
        }
    }
}
