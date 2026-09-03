using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Core.Entities;
using Vuelto.Core.Mail;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Email;

/// <summary>
/// EMAIL-2: the user-scoped connection rules. Every read/write is keyed by the caller's user id — the
/// entity is user-keyed (ADR-V002), so this handler, not a tenant filter, is the scope. Create-time
/// defaults (unread-only, import-from = now, cursor = import-from, 15-minute interval), validation
/// (provider, tokens, at-least-one filter, 5…1440-minute interval), one inbox per provider, and the
/// backfill rule: lowering <c>import_from</c> pulls the cursor back, raising it never advances the
/// cursor (that would silently skip un-imported mail).
/// </summary>
public sealed class EmailConnectionHandler(IRepository<EmailConnection> connections, IEmailTokenProtector tokens, TimeProvider clock)
{
    public const int MinIntervalMinutes = 5;
    public const int MaxIntervalMinutes = 24 * 60;

    public Task<List<EmailConnection>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        connections.Query().Where(c => c.UserId == userId).OrderBy(c => c.Provider).ToListAsync(cancellationToken);

    public Task<EmailConnection?> GetAsync(Guid userId, Guid id, CancellationToken cancellationToken) =>
        connections.Query().FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, cancellationToken);

    /// <summary>Stores a connection from freshly-acquired tokens (plaintext in, protected at rest).</summary>
    public async Task<(EmailConnection? Connection, ErrorResponse? Error)> CreateAsync(Guid userId, NewEmailConnection input, CancellationToken cancellationToken)
    {
        var provider = EmailProviders.Normalize(input.Provider);
        if (provider is null) return (null, new ErrorResponse("invalid_provider", "Provider must be 'microsoft' or 'google'."));
        if (string.IsNullOrWhiteSpace(input.AccessToken) || string.IsNullOrWhiteSpace(input.RefreshToken))
            return (null, new ErrorResponse("missing_tokens", "Both access and refresh tokens are required."));

        var senders = Clean(input.SenderFilters);
        var subjects = Clean(input.SubjectFilters);
        if (senders.Length == 0 && subjects.Length == 0)
            return (null, new ErrorResponse("filters_required", "Provide at least one sender or subject filter."));

        if (await connections.Query().AnyAsync(c => c.UserId == userId && c.Provider == provider, cancellationToken))
            return (null, new ErrorResponse("connection_exists", $"An inbox is already connected for {provider}."));

        var now = clock.GetUtcNow();
        var connection = new EmailConnection
        {
            UserId = userId,
            Provider = provider,
            AccountEmail = string.IsNullOrWhiteSpace(input.AccountEmail) ? null : input.AccountEmail.Trim(),
            AccessToken = tokens.Protect(input.AccessToken),
            RefreshToken = tokens.Protect(input.RefreshToken),
            TokenExpiresAt = input.TokenExpiresAt,
            SenderFilters = senders,
            SubjectFilters = subjects,
            UnreadOnly = true,
            ImportFrom = now,
            LastPolledAt = now, // the cursor starts at the scan boundary
            PollingIntervalMinutes = 15,
            Status = EmailConnectionStatuses.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await connections.AddAsync(connection, cancellationToken);
        await connections.SaveChangesAsync(cancellationToken);
        return (connection, null);
    }

    /// <summary>Edits folders/filters/unread/cursor/import-from/interval. Null connection = not found for this user.</summary>
    public async Task<(EmailConnection? Connection, ErrorResponse? Error)> UpdateAsync(Guid userId, Guid id, UpdateEmailConnectionRequest request, CancellationToken cancellationToken)
    {
        var connection = await GetAsync(userId, id, cancellationToken);
        if (connection is null) return (null, null);

        var senders = Clean(request.SenderFilters);
        var subjects = Clean(request.SubjectFilters);
        if (senders.Length == 0 && subjects.Length == 0)
            return (null, new ErrorResponse("filters_required", "Provide at least one sender or subject filter."));
        if (request.PollingIntervalMinutes is < MinIntervalMinutes or > MaxIntervalMinutes)
            return (null, new ErrorResponse("invalid_interval", $"Polling interval must be between {MinIntervalMinutes} and {MaxIntervalMinutes} minutes."));

        connection.Folders = Clean(request.Folders);
        connection.SenderFilters = senders;
        connection.SubjectFilters = subjects;
        connection.UnreadOnly = request.UnreadOnly;
        connection.IgnoreCursor = request.IgnoreCursor;
        connection.PollingIntervalMinutes = request.PollingIntervalMinutes;
        if (request.ImportFrom is { } from)
        {
            var fromUtc = from.ToUniversalTime();
            connection.ImportFrom = fromUtc;
            if (connection.LastPolledAt is null || fromUtc < connection.LastPolledAt.Value)
                connection.LastPolledAt = fromUtc; // backfill: pull the cursor back; never push it forward
        }
        connection.UpdatedAt = clock.GetUtcNow();
        connections.Update(connection);
        await connections.SaveChangesAsync(cancellationToken);
        return (connection, null);
    }

    /// <summary>Removes the connection (stops ingestion; imported transactions stay). False = not found for this user.</summary>
    public async Task<bool> DeleteAsync(Guid userId, Guid id, CancellationToken cancellationToken)
    {
        var connection = await GetAsync(userId, id, cancellationToken);
        if (connection is null) return false;
        connections.Remove(connection);
        await connections.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static string[] Clean(string[]? values) =>
        (values ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
