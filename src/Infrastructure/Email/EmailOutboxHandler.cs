using System.Text.Json;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Entities;

namespace Perezosoft.Infrastructure.Email;

/// <summary>
/// Outbox handler for <c>"email"</c> messages: deserializes the payload and performs the real SMTP
/// send via the underlying <see cref="SmtpEmailSender"/> (injected as the keyed <c>"smtp"</c>
/// <see cref="IEmailSender"/> so it isn't the outbox decorator itself).
/// <para>
/// At-least-once delivery means a duplicate dispatch can re-send a transactional email — accepted
/// here (a second magic link / invitation copy is harmless); the dispatcher only retries on failure.
/// </para>
/// </summary>
public sealed class EmailOutboxHandler(IEmailSender smtpSender) : IOutboxHandler
{
    public string Type => OutboxEmailSender.MessageType;

    public async Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Deserialize<EmailOutboxPayload>(message.Payload)
            ?? throw new InvalidOperationException($"Outbox message {message.Id} has an unreadable email payload.");

        await smtpSender.SendAsync(payload.To, payload.Subject, payload.HtmlBody, payload.InlineImages, cancellationToken);
    }
}
