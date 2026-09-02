namespace Perezosoft.Core.Abstractions;

/// <summary>
/// Records semantic audit events for the current tenant (OBS-4, ADR-008). The tenant is stamped
/// automatically (the event is <c>ITenantScoped</c>); pass the acting user explicitly. Like the outbox,
/// it <b>stages</b> the event on the current unit of work and does NOT save — so the audit commits
/// atomically with the change it records (or rolls back with it). <b>Never put secrets/PII in
/// <paramref name="metadata"/>.</b>
/// </summary>
public interface IAuditLog
{
    /// <param name="action">Stable verb, e.g. <c>"member.role_changed"</c>.</param>
    /// <param name="actorUserId">The acting user, or null for a system action.</param>
    /// <param name="entityType">Kind of entity acted on (optional).</param>
    /// <param name="entityId">Id of the entity acted on (optional).</param>
    /// <param name="metadata">Structured context, serialized to JSON. Identifiers only.</param>
    Task RecordAsync(
        string action,
        Guid? actorUserId = null,
        string? entityType = null,
        string? entityId = null,
        object? metadata = null,
        CancellationToken cancellationToken = default);
}
