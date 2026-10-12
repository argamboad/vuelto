namespace Vuelto.Infrastructure.Mail;

/// <summary>A transient provider error (429 / 5xx) — skip this poll, retry next cycle (EMAIL-3).</summary>
public sealed class MailTransientException(string? message = null, Exception? inner = null) : Exception(message ?? "Transient mail provider error.", inner);
