namespace Vuelto.Core.Vouchers;

// EMAIL-1 (port slice P9a, donor US-025): the pure voucher-parsing contracts. Parsing only ever
// PREPOPULATES a draft — the review queue (P10) surfaces an incomplete voucher for the user to finish,
// so a misparse is a fixable suggestion, never a corrupted budget (ADR-V010).

/// <summary>The bank a voucher came from.</summary>
public enum VoucherBank
{
    Unknown,
    Bac,
    BN
}

/// <summary>A provider-independent voucher email handed to the parser (the readers produce these). Read-only — parsing never touches the mailbox.</summary>
public record VoucherMessage(
    string MessageId,
    string? Subject,
    string? Sender,
    DateTimeOffset? ReceivedAt,
    string HtmlBody);

/// <summary>
/// The structured result of parsing a bank voucher. Every field is best-effort;
/// <see cref="MissingFields"/> lists the REQUIRED ones (merchant, amount, currency, date) that could
/// not be extracted or validated.
/// </summary>
public record ParsedVoucher
{
    public VoucherBank Bank { get; init; }
    public string? Merchant { get; init; }
    public decimal? Amount { get; init; }
    public string? Currency { get; init; }          // "CRC" | "USD"
    public DateOnly? Date { get; init; }
    public string? CardNumber { get; init; }
    public string? Authorization { get; init; }
    public string? Reference { get; init; }
    public string? TransactionType { get; init; }    // raw bank wording, e.g. "COMPRA" / "PAGO"

    public IReadOnlyList<string> MissingFields { get; init; } = Array.Empty<string>();

    /// <summary>True when nothing required is missing — a high-confidence parse.</summary>
    public bool IsComplete => MissingFields.Count == 0;
}

/// <summary>Stable identifiers for the extractors — the routing targets in <see cref="BankVoucherMap"/>. BN ships two distinct email formats.</summary>
public static class VoucherSources
{
    public const string Bac = "bac";
    public const string BnVoucher = "bn-voucher";
    public const string BnPayment = "bn-payment";
}

/// <summary>
/// The verified bank-voucher From-addresses observed on real BAC/BN emails. Used to pre-seed a
/// connection's sender filters so the provider query fetches only voucher mail; routing stays
/// subject-based.
/// </summary>
public static class KnownVoucherSenders
{
    public const string Bac = "notificacion@notificacionesbaccr.com";
    public const string BN = "bncontacto@bncr.fi.cr";

    public static readonly IReadOnlyList<string> All = new[] { Bac, BN };
}
