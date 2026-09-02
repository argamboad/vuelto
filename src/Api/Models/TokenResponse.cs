using System.Text.Json.Serialization;

namespace Perezosoft.Api.Models;

/// <summary>
/// OAuth-style token response returned by the token and refresh endpoints.
/// </summary>
public record TokenResponse
{
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    /// <summary>
    /// The rotated refresh token. Returned ONLY to native clients (desktop/mobile),
    /// which store it in the OS secure store in lieu of the HttpOnly cookie. Always
    /// null for the browser flow — there the refresh token never leaves the cookie.
    /// </summary>
    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; init; }

    [JsonPropertyName("token_type")]
    public string TokenType { get; init; } = "Bearer";

    [JsonPropertyName("expires_in")]
    public required int ExpiresIn { get; init; }

    [JsonPropertyName("user_id")]
    public Guid? UserId { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }
}
