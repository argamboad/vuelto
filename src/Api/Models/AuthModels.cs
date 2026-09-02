using System.Text.Json.Serialization;

namespace Vuelto.Api.Models;

public record EmailRequest(string Email, string? Culture = null);

public record OtpVerifyRequest(string Email, string Code);

public record RefreshRequest(
    [property: JsonPropertyName("refresh_token")] string? RefreshToken);

public record NativeExchangeRequest(
    [property: JsonPropertyName("code")] string Code);

public record LocaleRequest(
    [property: JsonPropertyName("locale")] string? Locale);

public record ThemeRequest(
    [property: JsonPropertyName("theme")] string? Theme);
