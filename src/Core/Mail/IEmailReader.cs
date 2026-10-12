using Vuelto.Core.Entities;

namespace Vuelto.Core.Mail;

/// <summary>
/// Reads filter-matching voucher emails from one connected inbox (EMAIL-3). One implementation per
/// provider (Microsoft Graph, Gmail); both honour the same filter semantics and return the
/// provider-independent <c>VoucherMessage</c> the parser consumes. <b>Read-only</b> — never marks read,
/// moves, or deletes (ADR-V010).
/// </summary>
public interface IEmailReader
{
    /// <summary>The provider this reader serves — see <see cref="EmailProviders"/>.</summary>
    string Provider { get; }

    /// <summary>
    /// Fetches the messages matching the connection's filters (folders AND sender/subject AND received ≥
    /// cursor AND, if set, unread). A 401 is refreshed once; a still-unauthorized connection is reported
    /// needs-reconsent, never thrown out of the poll loop.
    /// </summary>
    Task<EmailFetchResult> FetchAsync(EmailConnection connection, CancellationToken cancellationToken = default);

    /// <summary>Lists the account's real folders/labels for the picker. Same auth handling as <see cref="FetchAsync"/>.</summary>
    Task<EmailFoldersResult> ListFoldersAsync(EmailConnection connection, CancellationToken cancellationToken = default);
}
