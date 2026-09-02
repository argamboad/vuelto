namespace Perezosoft.Core.Entities;

/// <summary>
/// A user's notification delivery preferences (NOTIFY-2, ADR-013) — the per-user carve-out (ADR-C2),
/// like <see cref="User.Locale"/>. One row per user; both channels default on (absence ⇒ on). User PII
/// — wiped by account erasure (GDPR-2).
/// </summary>
public class NotificationPreference
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }

    public bool InAppEnabled { get; set; } = true;
    public bool EmailEnabled { get; set; } = true;
}
