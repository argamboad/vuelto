namespace Perezosoft.Core.Entities;

public class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Email { get; set; }

    /// <summary>Display name from the OAuth provider, refreshed each sign-in.</summary>
    public string? DisplayName { get; set; }

    /// <summary>True only when the provider asserts a verified email claim.</summary>
    public bool EmailVerified { get; set; }

    /// <summary>
    /// Preferred UI language (e.g. "en", "es"), or null to fall back to the browser/OS
    /// culture. A per-user preference that follows the user across devices.
    /// </summary>
    public string? Locale { get; set; }

    /// <summary>
    /// Preferred UI theme ("light", "dark" or "system"), or null when the user never chose
    /// one (which lets sign-in adopt a device-local choice — PREFS-1, ADR-022). A per-user
    /// preference that follows the user across devices.
    /// </summary>
    public string? Theme { get; set; }

    // Tenant membership is the source of truth for which tenant a user belongs to;
    // resolve it via TenantMembership (one tenant per user). See ITenantRepository.

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>OAuth identities linked to this account (one per provider).</summary>
    public ICollection<UserLogin> Logins { get; set; } = new List<UserLogin>();
}
