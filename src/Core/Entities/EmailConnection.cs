using Vuelto.Core.Mail;

namespace Vuelto.Core.Entities;

/// <summary>
/// A user's read-only connection to an email inbox the app scans for bank vouchers (EMAIL-2).
/// <b>User-keyed, not tenant-scoped</b> — the one deliberate exception (ADR-V002): a mailbox belongs to
/// a person, survives leaving or dissolving a household, and is wiped by account erasure through an
/// <c>IUserDataContributor</c>. Access/refresh tokens are stored <b>protected</b> (platform Data
/// Protection, ADR-V016) and are never returned to the client or logged.
/// </summary>
public class EmailConnection
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }

    /// <summary>OAuth provider — see <see cref="EmailProviders"/>.</summary>
    public required string Provider { get; set; }

    /// <summary>The mailbox this connection reads (display only).</summary>
    public string? AccountEmail { get; set; }

    /// <summary>Protected at rest.</summary>
    public required string AccessToken { get; set; }

    /// <summary>Protected at rest.</summary>
    public required string RefreshToken { get; set; }

    public DateTimeOffset TokenExpiresAt { get; set; }

    /// <summary>Mail folders/labels to scan (provider ids); empty = the inbox.</summary>
    public string[] Folders { get; set; } = [];

    /// <summary>
    /// Display names for <see cref="Folders"/>, index-aligned (provider ids are opaque — Graph folder ids,
    /// Gmail <c>Label_n</c>). Captured when the user applies a selection so the page can show what is
    /// scanned without another provider round-trip; readers never use them.
    /// </summary>
    public string[] FolderNames { get; set; } = [];

    /// <summary>Senders that identify voucher mail; pushed into the provider query.</summary>
    public string[] SenderFilters { get; set; } = [];

    /// <summary>Subject prefixes that identify voucher mail; pushed into the provider query.</summary>
    public string[] SubjectFilters { get; set; } = [];

    /// <summary>When true (default) only unread mail is scanned, so a backlog of already-handled vouchers is never imported.</summary>
    public bool UnreadOnly { get; set; } = true;

    /// <summary>Initial scan boundary — only mail received on/after this is considered (default = connection time; lower it for a deliberate backfill).</summary>
    public DateTimeOffset ImportFrom { get; set; }

    public int PollingIntervalMinutes { get; set; } = 15;

    /// <summary>When true the received-date cursor is ignored and every matching unread message is fetched (dedup prevents re-staging).</summary>
    public bool IgnoreCursor { get; set; }

    /// <summary>Time cursor — the last received-date scanned; starts at <see cref="ImportFrom"/>.</summary>
    public DateTimeOffset? LastPolledAt { get; set; }

    /// <summary>See <see cref="EmailConnectionStatuses"/>.</summary>
    public string Status { get; set; } = EmailConnectionStatuses.Active;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
