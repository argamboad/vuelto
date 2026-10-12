namespace Vuelto.Core.Entities;

/// <summary>
/// A per-user in-app notification (NOTIFY-1, ADR-013). Keyed by <see cref="UserId"/> — the ADR-C2
/// per-user carve-out, <b>not</b> tenant-scoped; a user only ever sees their own. User PII — wiped by
/// account erasure (GDPR-2). <see cref="Metadata"/> holds identifiers/context as JSON — never secrets.
/// </summary>
public class Notification
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }

    /// <summary>Stable verb, e.g. <c>"member.invited"</c>.</summary>
    public required string Kind { get; set; }

    public required string Title { get; set; }
    public string Body { get; set; } = string.Empty;

    /// <summary>Structured context as JSON (jsonb). Identifiers only — never secrets/PII.</summary>
    public string? Metadata { get; set; }

    /// <summary>When the user read it, or null if unread.</summary>
    public DateTimeOffset? ReadAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
