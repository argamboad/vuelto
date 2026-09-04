using Vuelto.Core.Entities;

namespace Vuelto.Core.Vouchers;

/// <summary>
/// EMAIL-5: the one matching rule. A merchant matches a rule when it <b>contains</b> the pattern,
/// case-insensitively; among the matches the <b>longest</b> pattern wins (most specific — "AUTOMERCADO
/// ESCAZU" beats "AUTOMERCADO"); ties resolve by the pattern text so the outcome is deterministic. A blank
/// merchant or no match → null. Pure, so staging and the mappings slice share one definition.
/// </summary>
public static class MerchantMatcher
{
    public static MerchantCategoryMapping? Resolve(IEnumerable<MerchantCategoryMapping> rules, string? merchant)
    {
        if (string.IsNullOrWhiteSpace(merchant)) return null;
        return rules
            .Where(r => !string.IsNullOrWhiteSpace(r.MerchantPattern)
                        && merchant.Contains(r.MerchantPattern.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.MerchantPattern.Trim().Length)
            .ThenBy(r => r.MerchantPattern, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }
}
