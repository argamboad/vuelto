using System.Text.Json;
using Vuelto.Core.Abstractions;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Infrastructure.Email;

/// <summary>
/// <see cref="IEmailSender"/> that ENQUEUES the email onto the transactional outbox instead of
/// sending it inline; the real SMTP send happens later on the dispatcher via
/// <see cref="EmailOutboxHandler"/> → <see cref="SmtpEmailSender"/> (ADR-007). This is the
/// app-facing <c>IEmailSender</c>, so existing call sites (passwordless, invitations) are unchanged
/// — but a request no longer blocks on SMTP or fails if the mail server is momentarily down.
/// <para>
/// The message is stamped with the ambient tenant (v4 audit H6): an invitation or an export carries a
/// household's content, and the stamp is what lets that household's dissolve remove it
/// (<c>OutboxDataContributor</c>). Mail sent outside any tenant — a sign-in code, a platform broadcast's
/// fan-out — stays tenant-less; account erasure removes a user's pending mail by address instead
/// (<c>OutboxUserDataContributor</c>, v4 audit H7).
/// </para>
/// </summary>
public sealed class OutboxEmailSender(IOutbox outbox, AppDbContext db, ICurrentTenant currentTenant) : IEmailSender
{
    /// <summary>The <see cref="Vuelto.Core.Entities.OutboxMessage.Type"/> for email messages.</summary>
    public const string MessageType = "email";

    public async Task SendAsync(string to, string subject, string htmlBody,
        IReadOnlyList<EmailInlineImage>? inlineImages = null, IReadOnlyList<EmailAttachment>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        // Reject before enqueueing: an oversize/malformed attachment would otherwise sit in the outbox
        // and fail every dispatch attempt until it dead-letters (JOBS-4).
        EmailAttachment.Validate(attachments, inlineImages);

        var payload = JsonSerializer.Serialize(
            new EmailOutboxPayload(to, subject, htmlBody, inlineImages, attachments));
        await outbox.EnqueueAsync(MessageType, payload, currentTenant.TenantId, cancellationToken);

        // The email call sites aren't wrapped in an explicit unit of work, so flush now to make the
        // enqueue durable before the request returns. SaveChanges on the shared context also commits
        // any pending business change alongside the message — exactly the atomicity we want.
        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Serialized form of an email on the outbox. <c>byte[]</c> image/attachment content is base64 in JSON.
/// <see cref="Attachments"/> is nullable and defaulted so payloads enqueued before JOBS-4 (which have
/// no <c>Attachments</c> property) still deserialize and send.
/// </summary>
public sealed record EmailOutboxPayload(
    string To, string Subject, string HtmlBody, IReadOnlyList<EmailInlineImage>? InlineImages,
    IReadOnlyList<EmailAttachment>? Attachments = null);
