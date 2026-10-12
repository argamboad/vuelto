using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Infrastructure.Audit;

/// <summary>
/// Tenant-data hook so the audit trail participates in tenant dissolve (OBS-4, ADR-008). Uses the
/// audited cross-tenant escape hatch (the dissolve runs for a tenant other than the current one) and a
/// set-based delete — which bypasses the append-only interceptor, so dissolve can purge a tenant's
/// events even though application code can't. (If a regulatory legal-hold is needed, export before wipe —
/// see the GDPR backlog item.)
/// </summary>
public sealed class AuditDataContributor(IRepository<AuditEvent> events) : ITenantDataContributor
{
    public Task<bool> HasDataAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        events.QueryAllTenants().AnyAsync(e => e.TenantId == tenantId, cancellationToken);

    public async Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        // Query() (not QueryAllTenants): the dissolve enters the target tenant (RLS-2/T6), so the filter
        // scopes this to it; composing QueryAllTenants() with a set-based write is banned (RLS-4/T7).
        await events.Query()
            .Where(e => e.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);

    public string ExportKey => "audit";

    // The audit trail is already secret-free (identifiers + metadata only), so it exports as-is.
    public async Task<object?> ExportAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await events.QueryAllTenants()
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.CreatedAt)
            .Select(e => new { e.Action, e.ActorUserId, e.ImpersonatedBy, e.EntityType, e.EntityId, e.Metadata, e.CreatedAt })
            .ToListAsync(cancellationToken);
}
