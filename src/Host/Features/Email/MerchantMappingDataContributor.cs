using Microsoft.EntityFrameworkCore;
using Perezosoft.Core.Abstractions;
using Vuelto.Host.Entities;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Vuelto.Host.Features.Email;

/// <summary>Tenant-data hook for the merchant → category rules: household configuration — wiped on dissolve, exported.</summary>
public sealed class MerchantMappingDataContributor(IRepository<MerchantCategoryMapping> mappings) : ITenantDataContributor
{
    /// <summary>A constant — the platform's export-key gate reads it from an uninitialized instance.</summary>
    public string ExportKey => "merchant_mappings";

    public Task<bool> HasDataAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        mappings.QueryAllTenants().AnyAsync(m => m.TenantId == tenantId, cancellationToken);

    public Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        mappings.Query().Where(m => m.TenantId == tenantId).ExecuteDeleteAsync(cancellationToken);

    public async Task<object?> ExportAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var rows = await mappings.QueryAllTenants()
            .Where(m => m.TenantId == tenantId)
            .OrderBy(m => m.PatternKey)
            .Select(m => new { m.Id, m.MerchantPattern, m.CategoryId, m.SuggestedClass, m.CreatedAt })
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : rows;
    }
}
