namespace Vuelto.Core.Vouchers;

/// <summary>
/// Extracts a <see cref="ParsedVoucher"/> from one bank's voucher email HTML. Implementations are
/// pure and <b>best-effort</b>: they never throw and never decide completeness (the
/// <see cref="IVoucherParser"/> facade validates) — so a single malformed email can never crash the
/// poll loop. Which messages reach an extractor is decided by <see cref="BankVoucherMap"/>.
/// </summary>
public interface IBankVoucherExtractor
{
    /// <summary>Stable routing id (see <see cref="VoucherSources"/>) — the map's target.</summary>
    string Key { get; }

    VoucherBank Bank { get; }

    /// <summary>Best-effort extraction; fields that can't be read are left null.</summary>
    ParsedVoucher Extract(string htmlBody);
}

/// <summary>
/// Routes a voucher email to the matching bank extractor and validates the result. Returns null when
/// no extractor recognizes the message (it is skipped downstream, never an error).
/// </summary>
public interface IVoucherParser
{
    ParsedVoucher? Parse(VoucherMessage message);
}
