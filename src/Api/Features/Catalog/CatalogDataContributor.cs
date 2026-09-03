using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Catalog;

/// <summary>
/// Tenant-data hook shared by both catalogs: the household's own names count as data, are wiped
/// on dissolve, and export as a list of <c>{ id, name, is_active }</c>.
/// </summary>
public abstract class CatalogDataContributor<TEntry>(IRepository<TEntry> entries) : ITenantDataContributor
    where TEntry : class, ICatalogEntry
{
    /// <summary>A constant per catalog — the platform's export-key gate reads it from an uninitialized instance.</summary>
    public abstract string ExportKey { get; }

    public Task<bool> HasDataAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        entries.QueryAllTenants().AnyAsync(e => e.TenantId == tenantId, cancellationToken);

    public async Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await entries.Query()
            .Where(e => e.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);

    public async Task<object?> ExportAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var rows = await entries.QueryAllTenants()
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.Name)
            .Select(e => new { e.Id, e.Name, e.IsActive, e.CreatedAt, e.UpdatedAt })
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : rows;
    }
}
