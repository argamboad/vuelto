using Vuelto.Core.Vouchers;

namespace Vuelto.Core.Entities;

/// <summary>
/// A user-maintained merchant → category rule (EMAIL-5, donor US-029 D4). When a voucher is staged
/// (EMAIL-4) its merchant is matched against these patterns to <b>suggest</b> a category and, optionally,
/// a class — copied onto the draft, never auto-applied; the user always confirms (EMAIL-6). Household-
/// scoped (ADR-V002): budget configuration belongs to the household, not the person. Matching is a
/// case-insensitive "contains" and the most specific (longest) matching pattern wins — see
/// <see cref="MerchantMatcher"/>. Dumb and transparent by design, not ML.
/// </summary>
public class MerchantCategoryMapping : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }

    /// <summary>The text matched (case-insensitive contains) against a voucher's merchant, as the user typed it.</summary>
    public required string MerchantPattern { get; set; }

    /// <summary>
    /// <see cref="MerchantPattern"/> trimmed and lower-cased — the uniqueness key: one rule per merchant
    /// text per household regardless of casing, enforced by a unique index (the donor used a functional
    /// <c>lower()</c> index; a stored key keeps the invariant in the EF model).
    /// </summary>
    public required string PatternKey { get; set; }

    /// <summary>The suggested category (FK → Categories, Restrict — categories soft-delete, ADR-V008).</summary>
    public Guid CategoryId { get; set; }

    /// <summary>Optional suggested class (<see cref="SuggestibleClasses"/>); null resolves to <c>budgeted</c> at staging.</summary>
    public string? SuggestedClass { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The uniqueness key for a pattern as typed.</summary>
    public static string KeyFor(string pattern) => pattern.Trim().ToLowerInvariant();
}
