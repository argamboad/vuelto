using System.Text.Json.Serialization;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Features.Email;

// EMAIL-2 wire shapes (snake_case, ADR-V012). A connection NEVER carries its tokens.

public record EmailConnectionResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("account_email")] string? AccountEmail,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("folders")] ConnectionFolder[] Folders,
    [property: JsonPropertyName("sender_filters")] string[] SenderFilters,
    [property: JsonPropertyName("subject_filters")] string[] SubjectFilters,
    [property: JsonPropertyName("unread_only")] bool UnreadOnly,
    [property: JsonPropertyName("ignore_cursor")] bool IgnoreCursor,
    [property: JsonPropertyName("import_from")] DateTimeOffset ImportFrom,
    [property: JsonPropertyName("polling_interval_minutes")] int PollingIntervalMinutes,
    [property: JsonPropertyName("last_polled_at")] DateTimeOffset? LastPolledAt,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt)
{
    public static EmailConnectionResponse From(EmailConnection c) => new(
        c.Id, c.Provider, c.AccountEmail, c.Status, ConnectionFolder.From(c), c.SenderFilters, c.SubjectFilters,
        c.UnreadOnly, c.IgnoreCursor, c.ImportFrom, c.PollingIntervalMinutes, c.LastPolledAt, c.CreatedAt);
}

/// <summary>
/// A scanned folder as the client sees it: the provider id the readers use plus the display name captured
/// when it was picked. A row saved before names were stored answers with its id as the name.
/// </summary>
public record ConnectionFolder([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("name")] string? Name)
{
    public static ConnectionFolder[] From(EmailConnection c) => c.Folders
        .Select((id, i) => new ConnectionFolder(id, i < c.FolderNames.Length && !string.IsNullOrWhiteSpace(c.FolderNames[i]) ? c.FolderNames[i] : id))
        .ToArray();
}

/// <summary>Editable scan settings — no token fields (tokens change only through reconnect).</summary>
public record UpdateEmailConnectionRequest(
    [property: JsonPropertyName("folders")] ConnectionFolder[]? Folders,
    [property: JsonPropertyName("sender_filters")] string[]? SenderFilters,
    [property: JsonPropertyName("subject_filters")] string[]? SubjectFilters,
    [property: JsonPropertyName("unread_only")] bool UnreadOnly = true,
    [property: JsonPropertyName("ignore_cursor")] bool IgnoreCursor = false,
    [property: JsonPropertyName("import_from")] DateTimeOffset? ImportFrom = null,
    [property: JsonPropertyName("polling_interval_minutes")] int PollingIntervalMinutes = 15);

/// <summary>Plaintext tokens + defaults for a new connection — built by the consent callback only, never bound from a client body.</summary>
public record NewEmailConnection(
    string Provider,
    string? AccountEmail,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset TokenExpiresAt,
    string[] SenderFilters,
    string[] SubjectFilters);

/// <summary>"Sync now" summary (EMAIL-4).</summary>
public record SyncResultResponse(
    [property: JsonPropertyName("staged")] int Staged,
    [property: JsonPropertyName("duplicates")] int Duplicates,
    [property: JsonPropertyName("unrecognized")] int Unrecognized);

public record AuthorizeResponse([property: JsonPropertyName("authorization_url")] string AuthorizationUrl);

public record FolderResponse([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("name")] string Name);

public record BankFilterPreset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sender_filters")] string[] SenderFilters,
    [property: JsonPropertyName("subject_filters")] string[] SubjectFilters);

public record SuggestedFiltersResponse(
    [property: JsonPropertyName("sender_filters")] string[] SenderFilters,
    [property: JsonPropertyName("subject_filters")] string[] SubjectFilters,
    [property: JsonPropertyName("banks")] BankFilterPreset[] Banks);
