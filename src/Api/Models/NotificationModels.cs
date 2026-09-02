using System.Text.Json;
using System.Text.Json.Serialization;
using Perezosoft.Core.Entities;

namespace Perezosoft.Api.Models;

public record NotificationResponse
{
    [JsonPropertyName("id")] public required Guid Id { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("body")] public required string Body { get; init; }
    [JsonPropertyName("metadata")] public JsonElement? Metadata { get; init; }
    [JsonPropertyName("read_at")] public DateTimeOffset? ReadAt { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }

    public static NotificationResponse From(Notification n) => new()
    {
        Id = n.Id,
        Kind = n.Kind,
        Title = n.Title,
        Body = n.Body,
        Metadata = n.Metadata is null ? null : JsonSerializer.Deserialize<JsonElement>(n.Metadata),
        ReadAt = n.ReadAt,
        CreatedAt = n.CreatedAt,
    };
}

public record UnreadCountResponse
{
    [JsonPropertyName("count")] public required int Count { get; init; }
}

public record NotificationsClearedResponse
{
    [JsonPropertyName("cleared")] public required int Cleared { get; init; }
}

public record NotificationPreferencesResponse
{
    [JsonPropertyName("in_app")] public required bool InApp { get; init; }
    [JsonPropertyName("email")] public required bool Email { get; init; }
}

public record UpdatePreferencesRequest
{
    [JsonPropertyName("in_app")] public bool InApp { get; init; } = true;
    [JsonPropertyName("email")] public bool Email { get; init; } = true;
}
