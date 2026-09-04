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

        var folders = CleanFolders(request.Folders, connection);
        connection.Folders = folders.Select(f => f.Id).ToArray();
        connection.FolderNames = folders.Select(f => f.Name).ToArray();
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

    /// <summary>
    /// Rows saved before folder names were stored carry ids alone. On read, resolve the missing names once
    /// from the provider's folder list and persist them, so an opaque id never reaches the page. Best
    /// effort: a dead token (needs reconsent), an unknown provider or a provider error leaves the row as
    /// is — the client then shows an "unnamed" placeholder, never the id. Returns true when names changed.
    /// </summary>
    public async Task<bool> BackfillFolderNamesAsync(EmailConnection connection, IEnumerable<IEmailReader> readers, CancellationToken cancellationToken)
    {
        if (!HasMissingFolderNames(connection)) return false;
        var reader = readers.FirstOrDefault(r => r.Provider == connection.Provider);
        if (reader is null) return false;

        EmailFoldersResult listed;
        try { listed = await reader.ListFoldersAsync(connection, cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
        if (listed.NeedsReconsent) return false;

        var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in listed.Folders) byId.TryAdd(f.Id, f.Name);

        var names = new string[connection.Folders.Length];
        var changed = false;
        for (var i = 0; i < names.Length; i++)
        {
            var stored = i < connection.FolderNames.Length ? connection.FolderNames[i] : "";
            names[i] = !string.IsNullOrWhiteSpace(stored) ? stored : byId.GetValueOrDefault(connection.Folders[i], "");
            changed |= names[i] != stored;
        }
        if (!changed && names.Length == connection.FolderNames.Length) return false;

        connection.FolderNames = names;
        connection.UpdatedAt = clock.GetUtcNow();
        connections.Update(connection);
        await connections.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static bool HasMissingFolderNames(EmailConnection c) =>
        c.FolderNames.Length < c.Folders.Length || c.FolderNames.Take(c.Folders.Length).Any(string.IsNullOrWhiteSpace);

    /// <summary>
    /// "Sync all": stages every one of the caller's inboxes in turn (each its own read → parse → dedup pass)
    /// and sums the counts. A dead inbox is counted under needs-reconsent (the staging pass flags the row)
    /// and never stops the others.
    /// </summary>
    public async Task<SyncAllResultResponse> SyncAllAsync(Guid userId, IVoucherStagingService staging, CancellationToken cancellationToken)
    {
        int synced = 0, reconsent = 0, staged = 0, duplicates = 0, unrecognized = 0;
        foreach (var connection in await ListAsync(userId, cancellationToken))
        {
            var result = await staging.StageConnectionAsync(connection, cancellationToken);
            if (result.NeedsReconsent) { reconsent++; continue; }
            synced++; staged += result.Staged; duplicates += result.Duplicates; unrecognized += result.Unrecognized;
        }
        return new SyncAllResultResponse(synced, reconsent, staged, duplicates, unrecognized);
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

    /// <summary>
    /// Trims + de-duplicates folders by id (case-insensitive, first wins). A blank name keeps the name
    /// already stored for that id, so a client that only knows ids never erases a captured name.
    /// </summary>
    private static List<(string Id, string Name)> CleanFolders(ConnectionFolder[]? values, EmailConnection existing)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < existing.Folders.Length && i < existing.FolderNames.Length; i++) known[existing.Folders[i]] = existing.FolderNames[i];

        var result = new List<(string Id, string Name)>();
        foreach (var f in values ?? [])
        {
            if (f is null || string.IsNullOrWhiteSpace(f.Id)) continue;
            var id = f.Id.Trim();
            if (result.Any(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
            var name = string.IsNullOrWhiteSpace(f.Name) ? known.GetValueOrDefault(id, "") : f.Name.Trim();
            result.Add((id, name));
        }
        return result;
    }
}
