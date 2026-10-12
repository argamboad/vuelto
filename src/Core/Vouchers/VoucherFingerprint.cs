using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Vuelto.Core.Vouchers;

/// <summary>
/// The household-scoped dedup key for a parsed voucher (EMAIL-4): a SHA-256 of
/// <c>bank | (authorization ?? reference) | amount | date</c>. Authorization is preferred (more stable
/// across re-sends); the hex digest keeps the unique index fixed-width and leaks nothing. When both ids
/// are absent (a misparse) the provider message id stands in so distinct purchases with the same
/// bank/amount/day still differ; with no message id either, <see langword="null"/> tells the caller to
/// skip dedup and stage anyway — under-dedup is recoverable, over-dedup is silent data loss.
/// </summary>
public static class VoucherFingerprint
{
    public static string? Compute(ParsedVoucher v, string? messageId = null)
    {
        var bank = v.Bank.ToString().ToLowerInvariant();
        var authOrRef = v.Authorization?.Trim() is { Length: > 0 } a ? a.ToLowerInvariant()
            : v.Reference?.Trim() is { Length: > 0 } r ? r.ToLowerInvariant()
            : null;

        string idPart;
        if (authOrRef is null)
        {
            if (string.IsNullOrEmpty(messageId)) return null;
            idPart = $"msgid:{messageId}";
        }
        else
        {
            idPart = authOrRef;
        }

        var amount = v.Amount?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
        var date = v.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{bank}|{idPart}|{amount}|{date}")));
    }
}
