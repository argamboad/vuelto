using Vuelto.Core.Budget;

namespace Vuelto.Core.Entities;

/// <summary>
/// A spend bucket the household classifies transactions and budget lines by. Soft-deleted
/// (<see cref="IsActive"/>), never hard-deleted — inactive names still label history (ADR-V008).
/// Seeded once per household from <see cref="SeedCatalog"/> in the caller's locale (ADR-V009).
/// </summary>
public class Category : ICatalogEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
