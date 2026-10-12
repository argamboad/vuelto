namespace Vuelto.Api.Services;

/// <summary>The standard API error envelope. Serializes to <c>{ "error": ..., "message": ... }</c>
/// (camelCase) — the shape every error response uses.</summary>
public record ErrorResponse(string Error, string Message);
