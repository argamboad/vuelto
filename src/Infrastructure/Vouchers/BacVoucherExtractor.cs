using Vuelto.Core.Vouchers;

namespace Vuelto.Infrastructure.Vouchers;

/// <summary>
/// BAC Credomatic card-transaction voucher ("Notificación de transacción"): scans every label/value
/// row with a normalized label, so accent/case/colon/layout tweaks don't break it. Never throws.
/// </summary>
public sealed class BacVoucherExtractor : IBankVoucherExtractor
{
    public string Key => VoucherSources.Bac;
    public VoucherBank Bank => VoucherBank.Bac;

    public ParsedVoucher Extract(string htmlBody)
    {
        try
        {
            string? merchant = null, currency = null, card = null, auth = null, reference = null, txType = null;
            decimal? amount = null;
            DateOnly? date = null;

            foreach (var (label, value) in HtmlVouchers.LabelValueRows(htmlBody))
            {
                switch (label)
                {
                    case "COMERCIO": merchant = value.Trim(); break;
                    case "FECHA": date = SpanishDateParser.TryParse(value); break;
                    case "AUTORIZACION": auth = value.Trim(); break;
                    case "REFERENCIA": reference = value.Trim(); break;
                    case "TIPO DE TRANSACCION": txType = value.Trim().ToUpperInvariant(); break;
                    case "MONTO":
                        if (VoucherText.TryParseMoney(value, out var cur, out var amt)) { currency = cur; amount = amt; }
                        break;
                    default:
                        if (VoucherText.IsCardBrand(label)) card = value.Trim();
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
