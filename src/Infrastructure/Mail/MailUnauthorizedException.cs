namespace Vuelto.Infrastructure.Mail;

/// <summary>A provider returned 401 — the access token needs refreshing (EMAIL-3).</summary>
public sealed class MailUnauthorizedException(string? message = null) : Exception(message ?? "Mail request was unauthorized (401).");
