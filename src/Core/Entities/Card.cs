using Vuelto.Core.Budget;

namespace Vuelto.Core.Entities;

/// <summary>
/// A payment card the household spends with (CARDS-1, ADR-V021): identified by its <see cref="Brand"/> and the
/// <see cref="Last4"/> digits a bank voucher prints, named by the household (<see cref="Name"/> is the alias —
/// "VISA-1234" until someone renames it). A catalog entry like a bank: soft-deleted, unique per household, never
/// seeded. A transaction may name one (optional — cash and account transfers have none); a voucher confirm links
/// the card it reads, creating it on first sight.
/// </summary>
public class Card : ICatalogEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    /// <summary>The alias the household sees.</summary>
    public required string Name { get; set; }
    /// <summary>The card's current number as the household sees it — the newest identity (<see cref="CardIdentity"/> holds every one the bank has printed, so a renewal keeps the card).</summary>
    public required string Brand { get; set; }
    public required string Last4 { get; set; }
    public Guid? BankId { get; set; }
    /// <summary>True while the alias is still the automatic <c>BRAND-1234</c>; cleared by the first rename.</summary>
    public bool AutoNamed { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
