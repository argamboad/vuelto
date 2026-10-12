using System.Text.Json.Serialization;

namespace Vuelto.Api.Models;

/// <summary>
/// Current user profile (GET /api/auth/me) — surfaced to the client top bar.
/// </summary>
public record UserProfileResponse
{
    [JsonPropertyName("userName")]
    public required string UserName { get; init; }

    [JsonPropertyName("tenantName")]
    public required string TenantName { get; init; }
}
