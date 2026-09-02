namespace Vuelto.Core.Entities;

/// <summary>
/// A tenant-scoped API key for programmatic access (PUBAPI, ADR-015) — the non-interactive counterpart
/// to a user session. Only the <b>hash</b> of the key is stored (like refresh/invitation tokens); the raw
/// value is shown once at creation and never again. A presented key authenticates as its tenant (the
/// auth handler mints a principal carrying the <c>tenant_id</c> claim), carrying the <see cref="Scopes"/>
/// it was granted. Revoke by stamping <see cref="RevokedAt"/>; keys may also carry an <see cref="ExpiresAt"/>.
/// </summary>
public class ApiKey : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }

    /// <summary>Human label for the key (e.g. "CI deploy", "Zapier") — shown in the management list.</summary>
    public required string Name { get; set; }

    /// <summary>Deterministic hash of the raw key (the raw value is never stored). Used for O(1) lookup.</summary>
    public required string KeyHash { get; set; }

    /// <summary>A short, non-secret prefix of the raw key (e.g. <c>pk_ab12cd</c>) for display/identification.</summary>
    public required string Prefix { get; set; }

    /// <summary>Comma-separated <c>ApiScopes</c> the key is granted (e.g. "read,write").</summary>
    public required string Scopes { get; set; }

    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last time the key successfully authenticated a request (best-effort; for the owner's view).</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Optional expiry; a key past this is inactive even if not revoked.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Set when the key is revoked; a revoked key never authenticates again.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Whether the key can currently authenticate: not revoked and not past expiry.</summary>
    public bool IsActive(DateTimeOffset now) => RevokedAt is null && (ExpiresAt is null || ExpiresAt > now);
}

/// <summary>
/// Example API-key scopes (PUBAPI). Scopes gate public endpoints via <c>.RequireApiScope(...)</c> — a key
/// only reaches an endpoint whose scope it holds. Replace/extend with the app's real scopes.
/// </summary>
public static class ApiScopes
{
    public const string Read = "read";
    public const string Write = "write";

    /// <summary>All scopes the platform knows about — the default grant when none is specified.</summary>
    public static readonly IReadOnlyList<string> All = [Read, Write];

    /// <summary>Parses a stored comma-separated scope string into a set (trimmed, lowercased, de-duped).</summary>
    public static IReadOnlySet<string> Parse(string? scopes) =>
        (scopes ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => s.ToLowerInvariant())
        .ToHashSet();
}
