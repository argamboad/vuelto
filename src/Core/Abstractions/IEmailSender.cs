namespace Perezosoft.Core.Abstractions;

public interface IEmailSender
{
    /// <summary>
    /// Sends an HTML email. <paramref name="inlineImages"/> are embedded in the message
    /// (multipart/related) and referenced from the HTML via <c>cid:{ContentId}</c> — the
    /// only logo-embedding approach mainstream clients (Gmail/Outlook) render reliably.
    /// <para>
    /// Throws <see cref="EmailSendException"/> if delivery to the SMTP server fails; a hung
    /// server is bounded by a per-send timeout rather than stalling the request indefinitely.
    /// </para>
    /// </summary>
    Task SendAsync(string to, string subject, string htmlBody,
        IReadOnlyList<EmailInlineImage>? inlineImages = null, CancellationToken cancellationToken = default);
}

/// <summary>An image embedded in an email and referenced from the HTML as <c>cid:{ContentId}</c>.</summary>
public sealed record EmailInlineImage(string ContentId, string FileName, byte[] Content, string MediaType);

/// <summary>Thrown when an email could not be delivered to the SMTP server (timeout, connect/auth/send failure).</summary>
public sealed class EmailSendException(string message, Exception innerException)
    : Exception(message, innerException);
