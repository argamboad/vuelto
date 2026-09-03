using Vuelto.Core.Budget;

namespace Vuelto.Core.Entities;

/// <summary>
/// A money source — a bank or <c>Cash</c>. Every transaction must name one (ADR-V007). Soft-deleted
/// (<see cref="IsActive"/>) like categories (ADR-V008); seeded once per household from
/// <see cref="SeedCatalog"/> (Cash + the common Costa Rican banks) in the caller's locale.
/// </summary>
public class Bank : ICatalogEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
