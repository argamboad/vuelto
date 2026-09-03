using System.Globalization;
using System.Text;

namespace Vuelto.Core.Budget;

/// <summary>
/// REPORTS-2 (donor US-044, WU-4 B5): renders export rows as RFC 4180 CSV. Fixed column order, UTF-8,
/// CRLF line ends, amounts as plain decimals with two places and no currency symbol, the frozen rate
/// with <b>four</b> places (it is stored as NUMERIC(10,4), so the two amounts can be reproduced from
/// it), dates as <c>yyyy-MM-dd</c>. Text fields are quoted only when they need it.
/// </summary>
public static class TransactionCsvWriter
{
    public const string Header = "date,payee,category,class,amount_crc,amount_usd,exchange_rate_used,payment_method,bank,source";

    public static string Write(IEnumerable<TransactionExportRow> rows)
    {
        var sb = new StringBuilder();
        sb.Append(Header).Append("\r\n");
        foreach (var r in rows)
        {
            sb.Append(r.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
              .Append(Escape(r.Payee)).Append(',')
              .Append(Escape(r.CategoryName)).Append(',')
              .Append(Escape(r.TransactionType)).Append(',')
              .Append(r.AmountCrc.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.AmountUsd.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
              .Append(r.ExchangeRateUsed.ToString("F4", CultureInfo.InvariantCulture)).Append(',')
              .Append(Escape(r.PaymentMethod)).Append(',')
              .Append(Escape(r.BankName)).Append(',')
              .Append(Escape(r.Source)).Append("\r\n");
        }
        return sb.ToString();
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}
