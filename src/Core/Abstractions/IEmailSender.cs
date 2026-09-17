namespace Vuelto.Core.Abstractions;

public interface IEmailSender
{
    /// <summary>
    /// Sends an HTML email. <paramref name="inlineImages"/> are embedded in the message
    /// (multipart/related) and referenced from the HTML via <c>cid:{ContentId}</c> — the
    /// only logo-embedding approach mainstream clients (Gmail/Outlook) render reliably.
    /// <paramref name="attachments"/> are ordinary file attachments (e.g. a PDF report); their
    /// combined size is capped at <see cref="EmailAttachment.MaxTotalBytes"/> and every entry needs
    /// a non-blank file name and media type — otherwise the call throws <see cref="ArgumentException"/>
    /// before anything is queued or sent (see <see cref="EmailAttachment.Validate"/>).
    /// <para>
    /// Throws <see cref="EmailSendException"/> if delivery to the SMTP server fails; a hung
    /// server is bounded by a per-send timeout rather than stalling the request indefinitely.
    /// </para>
    /// <para>
    /// Pass <paramref name="cancellationToken"/> <b>by name</b> — it follows two optional lists, so a
    /// positional token no longer lines up with it (JOBS-4 moved it one slot to the right).
    /// </para>
    /// </summary>
    Task SendAsync(string to, string subject, string htmlBody,
        IReadOnlyList<EmailInlineImage>? inlineImages = null,
        IReadOnlyList<EmailAttachment>? attachments = null,
        CancellationToken cancellationToken = default);
}

/// <summary>An image embedded in an email and referenced from the HTML as <c>cid:{ContentId}</c>.</summary>
public sealed record EmailInlineImage(string ContentId, string FileName, byte[] Content, string MediaType);

/// <summary>A file attached to an email (shown as a downloadable attachment, not referenced from the HTML).</summary>
public sealed record EmailAttachment(string FileName, byte[] Content, string MediaType)
{
    /// <summary>
    /// Upper bound on the combined size of one email's attachments: 10 MiB, the transactional
    /// relay's (Brevo) limit. Enforced before enqueueing, so an oversize mail never sits in the outbox.
    /// </summary>
    public const int MaxTotalBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Guard every <see cref="IEmailSender"/> runs before queueing or sending. Throws
    /// <see cref="ArgumentException"/> (<c>ParamName = "attachments"</c>) for a null entry, a blank
    /// file name or media type, missing content, or a combined size over <see cref="MaxTotalBytes"/>.
    /// A null or empty list is valid.
    /// </summary>
    public static void Validate(IReadOnlyList<EmailAttachment>? attachments)
    {
        if (attachments is null) return;

        long total = 0;
        for (var i = 0; i < attachments.Count; i++)
        {
            var a = attachments[i]
                ?? throw new ArgumentException($"Attachment #{i} is null.", nameof(attachments));
            if (string.IsNullOrWhiteSpace(a.FileName))
                throw new ArgumentException($"Attachment #{i} has no file name.", nameof(attachments));
            if (string.IsNullOrWhiteSpace(a.MediaType))
                throw new ArgumentException($"Attachment '{a.FileName}' has no media type.", nameof(attachments));
            if (a.Content is null)
                throw new ArgumentException($"Attachment '{a.FileName}' has no content.", nameof(attachments));
            total += a.Content.LongLength;
        }

        if (total > MaxTotalBytes)
            throw new ArgumentException(
                $"Email attachments total {total} bytes, over the {MaxTotalBytes}-byte limit.", nameof(attachments));
    }
}

/// <summary>Thrown when an email could not be delivered to the SMTP server (timeout, connect/auth/send failure).</summary>
public sealed class EmailSendException(string message, Exception innerException)
    : Exception(message, innerException);
