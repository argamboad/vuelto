namespace Vuelto.Core.Vouchers;

/// <summary>
/// One routing rule: a (sender, subject) pair that points an incoming email at the extractor that can
/// parse it. Match is on the fields that are set — sender is a case-insensitive substring (a domain or
/// a full address), subject a case-insensitive prefix (so BAC's "Notificación de transacción …" suffix
/// still matches). A rule with neither field set never matches (no accidental catch-all).
/// </summary>
public sealed record VoucherRoutingRule(string ExtractorKey, string? SenderContains = null, string? SubjectPrefix = null)
{
    public bool Matches(string? sender, string? subject)
    {
        var hasSender = !string.IsNullOrWhiteSpace(SenderContains);
        var hasSubject = !string.IsNullOrWhiteSpace(SubjectPrefix);
        if (!hasSender && !hasSubject) return false;

        if (hasSender && (string.IsNullOrWhiteSpace(sender) || sender.IndexOf(SenderContains!, StringComparison.OrdinalIgnoreCase) < 0))
            return false;
        if (hasSubject && (string.IsNullOrWhiteSpace(subject) || !subject.Trim().StartsWith(SubjectPrefix!, StringComparison.OrdinalIgnoreCase)))
            return false;
        return true;
    }
}

/// <summary>
/// Maps an incoming voucher email to the extractor that should parse it by matching sender + subject
/// against an ordered rule list. Routing is data, not code — a household-specific rule set can replace
/// <see cref="Default"/> later without touching an extractor. Null when nothing matches.
/// </summary>
public sealed class BankVoucherMap
{
    private readonly IReadOnlyList<VoucherRoutingRule> _rules;

    public BankVoucherMap(IEnumerable<VoucherRoutingRule> rules) => _rules = rules.ToList();

    public IReadOnlyList<VoucherRoutingRule> Rules => _rules;

    /// <summary>Distinct non-empty subject prefixes — pre-seed for connection filters.</summary>
    public IReadOnlyList<string> SubjectFilters =>
        _rules.Where(r => !string.IsNullOrWhiteSpace(r.SubjectPrefix)).Select(r => r.SubjectPrefix!).Distinct().ToList();

    /// <summary>Distinct non-empty sender hints — pre-seed for connection filters.</summary>
    public IReadOnlyList<string> SenderFilters =>
        _rules.Where(r => !string.IsNullOrWhiteSpace(r.SenderContains)).Select(r => r.SenderContains!).Distinct().ToList();

    /// <summary>
    /// The built-in Costa Rican bank rules. Subject-only on purpose: a guessed sender would silently
    /// drop real vouchers; the verified From-addresses (<see cref="KnownVoucherSenders"/>) seed the
    /// FETCH filters instead.
    /// </summary>
    public static BankVoucherMap Default { get; } = new(
    [
        new VoucherRoutingRule(VoucherSources.Bac, SubjectPrefix: "Notificación de transacción"),
        new VoucherRoutingRule(VoucherSources.BnVoucher, SubjectPrefix: "Voucher Digital"),
        new VoucherRoutingRule(VoucherSources.BnPayment, SubjectPrefix: "BN Conectividad le informa"),
    ]);

    /// <summary>The extractor key for the first rule that matches, or null.</summary>
    public string? Resolve(string? sender, string? subject) => _rules.FirstOrDefault(r => r.Matches(sender, subject))?.ExtractorKey;
}
