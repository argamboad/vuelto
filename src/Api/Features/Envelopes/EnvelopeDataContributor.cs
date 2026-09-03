using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Envelopes;

/// <summary>
/// Tenant-data hook for envelopes: the household's buckets count as data, are wiped on dissolve,
/// and export with their targets and cadence. (Slices stay self-contained — R7 — so this does not
/// inherit the catalog slice's base class.)
/// </summary>
public sealed class EnvelopeDataContributor(IRepository<Envelope> envelopes) : ITenantDataContributor
{
    /// <summary>A constant — the platform's export-key gate reads it from an uninitialized instance.</summary>
    public string ExportKey => "envelopes";

    public Task<bool> HasDataAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        envelopes.QueryAllTenants().AnyAsync(e => e.TenantId == tenantId, cancellationToken);

    public async Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await envelopes.Query()
            .Where(e => e.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);

    public async Task<object?> ExportAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var rows = await envelopes.QueryAllTenants()
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.Name)
            .Select(e => new { e.Id, e.Name, e.AnnualTargetCrc, e.AnnualTargetUsd, e.ReminderCadence, e.IsActive, e.CreatedAt, e.UpdatedAt })
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : rows;
    }
}
