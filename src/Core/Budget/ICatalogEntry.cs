using Vuelto.Core.Entities;

namespace Vuelto.Core.Budget;

/// <summary>
/// A soft-deleted, household-scoped name catalog entry (ADR-V008): categories and banks share this
/// shape, so one generic handler serves both. Names are unique per household, case-insensitively;
/// <see cref="IsActive"/> false hides the entry from pickers but keeps historical rows readable.
/// </summary>
public interface ICatalogEntry : ITenantScoped
{
    Guid Id { get; set; }
    new Guid TenantId { get; set; }
    string Name { get; set; }
    bool IsActive { get; set; }
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}
