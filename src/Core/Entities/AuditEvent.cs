namespace Perezosoft.Core.Entities;

/// <summary>
/// An append-only record of a security/compliance-relevant action within a tenant (OBS-4, ADR-008):
/// member invited/removed, role changed, subscription changed, tenant dissolved, etc. <c>ITenantScoped</c>,
/// so the global query filter shows a tenant only its own trail. <b>Append-only</b> — application code
/// may insert but never update or delete (enforced by <c>AuditAppendOnlyInterceptor</c>); tenant dissolve
/// removes a tenant's events via the set-based escape hatch, which bypasses the change tracker.
/// <para>
/// <b>Audit ≠ logs:</b> this is durable, queryable, exportable tenant data, not operational telemetry.
/// Put identifiers and context in <see cref="Metadata"/> — <b>never secrets or PII beyond actor refs</b>.
/// </para>
/// </summary>
public class AuditEvent : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }

    /// <summary>The user who performed the action, if any (a system action has none).</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>
    /// When the action was performed during an admin impersonation session ("sign in as", ADR-014), the
    /// staff user actually driving it — <see cref="ActorUserId"/> stays the impersonated user so tenant
    /// history reads naturally, and this marks the real hands on the keyboard (v3 audit LB-ADM-1). Null
    /// for a normal action. Stamped ambiently by the audit log from the <c>impersonated_by</c> JWT claim.
    /// </summary>
    public Guid? ImpersonatedBy { get; set; }

    /// <summary>What happened, e.g. <c>"member.role_changed"</c>. Stable, greppable verbs.</summary>
    public required string Action { get; set; }

    /// <summary>The kind of entity acted on, e.g. <c>"TenantMembership"</c> (optional).</summary>
    public string? EntityType { get; set; }

    /// <summary>The id of the entity acted on (optional).</summary>
    public string? EntityId { get; set; }

    /// <summary>Structured context as JSON (jsonb). Identifiers only — never secrets/PII.</summary>
    public string? Metadata { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
