using System.Text.Json;
using Perezosoft.Core.Abstractions;
using Perezosoft.Infrastructure.Persistence;

namespace Perezosoft.Infrastructure.Email;

/// <summary>
/// <see cref="IEmailSender"/> that ENQUEUES the email onto the transactional outbox instead of
/// sending it inline; the real SMTP send happens later on the dispatcher via
/// <see cref="EmailOutboxHandler"/> → <see cref="SmtpEmailSender"/> (ADR-007). This is the
/// app-facing <c>IEmailSender</c>, so existing call sites (passwordless, invitations) are unchanged
/// — but a request no longer blocks on SMTP or fails if the mail server is momentarily down.
/// </summary>
public sealed class OutboxEmailSender(IOutbox outbox, AppDbContext db) : IEmailSender
{
    /// <summary>The <see cref="Perezosoft.Core.Entities.OutboxMessage.Type"/> for email messages.</summary>
    public const string MessageType = "email";

    public async Task SendAsync(string to, string subject, string htmlBody,
        IReadOnlyList<EmailInlineImage>? inlineImages = null, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new EmailOutboxPayload(to, subject, htmlBody, inlineImages));
        await outbox.EnqueueAsync(MessageType, payload, cancellationToken: cancellationToken);

        // The email call sites aren't wrapped in an explicit unit of work, so flush now to make the
        // enqueue durable before the request returns. SaveChanges on the shared context also commits
        // any pending business change alongside the message — exactly the atomicity we want.
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Serialized form of an email on the outbox. <c>byte[]</c> image content is base64 in JSON.</summary>
public sealed record EmailOutboxPayload(
    string To, string Subject, string HtmlBody, IReadOnlyList<EmailInlineImage>? InlineImages);
