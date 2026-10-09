namespace Vuelto.Core.Entities;

/// <summary>
/// A masked number whose digits are not the last four (#210, ADR-V027) — BN payments print <c>XXXXXXXXXXX8755X</c> for a
/// card that ends in 7558 — mapped to the <see cref="Card"/> the household said it is. The review queue asks once,
/// the confirm remembers the answer here, and the next voucher printing the same pattern books on that card. A later,
/// different answer replaces it. Unique per household; goes with its card (a merge moves it to the survivor).
/// </summary>
public class CardPattern : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid CardId { get; set; }

    /// <summary>The number as <c>CardIdentity.Read</c> normalizes it: upper-case, layout removed, every mask an <c>X</c>.</summary>
    public required string Pattern { get; set; }

    public const int PatternMaxLength = 32;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
