using Microsoft.Extensions.Logging;
using Vuelto.Core.Vouchers;

namespace Vuelto.Infrastructure.Vouchers;

/// <summary>
/// Routes a voucher email to an extractor via <see cref="BankVoucherMap"/> (sender + subject), then
/// validates the result into <see cref="ParsedVoucher.MissingFields"/>. Null when nothing matches —
/// those messages are skipped downstream. Extraction is wrapped so a single malformed email can't
/// crash the poll loop.
/// </summary>
public sealed class VoucherParser(IEnumerable<IBankVoucherExtractor> extractors, BankVoucherMap map, ILogger<VoucherParser>? logger = null) : IVoucherParser
{
    private static readonly string[] AllowedCurrencies = ["CRC", "USD"];
    private readonly IReadOnlyDictionary<string, IBankVoucherExtractor> _byKey = extractors.ToDictionary(e => e.Key, StringComparer.OrdinalIgnoreCase);

    public ParsedVoucher? Parse(VoucherMessage message)
    {
        if (message is null) return null;

        var key = map.Resolve(message.Sender, message.Subject);
        if (key is null || !_byKey.TryGetValue(key, out var extractor))
        {
            logger?.LogDebug("No voucher extractor mapped for sender {Sender} / subject {Subject}", message.Sender, message.Subject);
            return null;
        }

        ParsedVoucher parsed;
        try
        {
            parsed = extractor.Extract(message.HtmlBody ?? string.Empty);
        }
        catch (Exception ex)
        {
            // Defense in depth — extractors already swallow their own errors.
            logger?.LogWarning(ex, "Voucher extractor {Bank} threw on message {MessageId}", extractor.Bank, message.MessageId);
            parsed = new ParsedVoucher { Bank = extractor.Bank };
        }

        return parsed with { MissingFields = Validate(parsed) };
    }

    private static IReadOnlyList<string> Validate(ParsedVoucher v)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(v.Merchant)) missing.Add(nameof(ParsedVoucher.Merchant));
        if (v.Amount is not > 0m) missing.Add(nameof(ParsedVoucher.Amount));
        if (v.Currency is null || Array.IndexOf(AllowedCurrencies, v.Currency) < 0) missing.Add(nameof(ParsedVoucher.Currency));
        if (v.Date is null) missing.Add(nameof(ParsedVoucher.Date));
        return missing;
    }
}
