using System.Text.Json;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Infrastructure.Audit;

/// <summary>
/// EF implementation of <see cref="IAuditLog"/> (OBS-4, ADR-008). Stages an <see cref="AuditEvent"/> on
/// the shared request-scoped context without saving, so it commits in the same transaction as the
/// audited change. <c>TenantId</c> is stamped by the tenant interceptor; <see cref="AuditEvent.Metadata"/>
/// is the serialized JSON of the supplied object.
/// </summary>
public sealed class AuditLog(
    IRepository<AuditEvent> events,
    TimeProvider clock,
    ICurrentImpersonation? impersonation = null) : IAuditLog
{
    public async Task RecordAsync(
        string action,
        Guid? actorUserId = null,
        string? entityType = null,
        string? entityId = null,
        object? metadata = null,
        CancellationToken cancellationToken = default)
    {
        await events.AddAsync(new AuditEvent
        {
            Action = action,
            ActorUserId = actorUserId,
            // Ambient, not a parameter (LB-ADM-1): a write performed during an impersonation session is
            // stamped with the REAL actor on EVERY event — no call site can forget the attribution.
            ImpersonatedBy = impersonation?.ImpersonatedBy,
            EntityType = entityType,
            EntityId = entityId,
            Metadata = metadata is null ? null : JsonSerializer.Serialize(metadata),
            CreatedAt = clock.GetUtcNow(),
        }, cancellationToken);
        // Intentionally no SaveChanges — the audit persists with the caller's unit of work.
    }
}
