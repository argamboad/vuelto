namespace Vuelto.Core.Entities;

/// <summary>
/// One (brand, last four) pair a bank has printed for a <see cref="Card"/> (CARDS-1, ADR-V021). A renewed card gets
/// a new number but is the same card to the household, so a card owns a list of identities: the voucher path
/// matches on any of them, and merging two cards moves the identities (and the transactions) under the survivor.
/// Unique per household — one pair can only ever name one card.
/// </summary>
public class CardIdentity : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid CardId { get; set; }
    public required string Brand { get; set; }
    public required string Last4 { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
