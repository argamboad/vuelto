namespace Vuelto.Api.Configuration;

/// <summary>
/// Platform-staff allowlist (ADR-014), bound from <c>Admin</c>. Staff are configured <b>out-of-band</b>
/// (env/.env), never from the app and never as a tenant role — the highest privilege is granted only by
/// whoever controls deployment config. Empty ⇒ no platform staff.
/// </summary>
public sealed class PlatformAdminSettings
{
    /// <summary>Emails allowed to use the admin surface. Compared case-insensitively.</summary>
    public string[] StaffEmails { get; set; } = [];
}
