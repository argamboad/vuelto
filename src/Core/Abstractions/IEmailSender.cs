namespace Vuelto.Core.Abstractions;

public interface IEmailSender
{
    /// <summary>
    /// Sends an HTML email. <paramref name="inlineImages"/> are embedded in the message
    /// (multipart/related) and referenced from the HTML via <c>cid:{ContentId}</c> — the
    /// only logo-embedding approach mainstream clients (Gmail/Outlook) render reliably.
    /// <paramref name="attachments"/> are ordinary file attachments (e.g. a PDF report); their
    /// combined raw size (inline images included) is capped at <see cref="EmailAttachment.MaxTotalBytes"/>,
    /// at most <see cref="EmailAttachment.MaxCount"/> parts, and every entry needs a safe base file name and a
    /// <c>type/subtype</c> media type — otherwise the call throws <see cref="ArgumentException"/>
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
    /// Upper bound on the combined RAW size of one email's attachments and inline images: 7 MiB. The
    /// transactional relay (Brevo) caps the encoded MESSAGE at <see cref="RelayLimitBytes"/>, and base64 is
    /// 4/3 the raw size plus a CRLF every 76 characters — 10 MiB of raw bytes was ~13.7 MiB on the wire, refused
    /// after five retries (v4 T40). Enforced before enqueueing, so an oversize mail never sits in the outbox.
    /// </summary>
    public const int MaxTotalBytes = 7 * 1024 * 1024;

    /// <summary>The transactional relay's (Brevo) cap on the whole encoded message.</summary>
    public const int RelayLimitBytes = 10 * 1024 * 1024;

    /// <summary>The most parts (attachments plus inline images) one email may carry.</summary>
    public const int MaxCount = 20;

    /// <summary>The longest file name the MIME header gets.</summary>
    public const int MaxFileNameLength = 255;

    /// <summary>What <paramref name="rawBytes"/> become on the wire: base64 (4/3) with a CRLF every 76 characters.</summary>
    public static long EncodedSize(long rawBytes)
    {
        var base64 = (rawBytes + 2) / 3 * 4;
        return base64 + (base64 + 75) / 76 * 2;
    }

    /// <summary>
    /// The file name as it goes into the MIME header (v4 T40, LB-JOBS-4): a base name only — a slash OR a
    /// backslash is a separator — with control characters dropped, trimmed, at most
    /// <see cref="MaxFileNameLength"/> characters. <c>../../etc/passwd</c> used to land in the header verbatim.
    /// </summary>
    public string SafeFileName => ToSafeFileName(FileName);

    public static string ToSafeFileName(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return string.Empty;
        var cut = fileName.LastIndexOfAny(['/', '\\']);
        var baseName = cut < 0 ? fileName : fileName[(cut + 1)..];
        var clean = new string(baseName.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length <= MaxFileNameLength ? clean : clean[..MaxFileNameLength];
    }

    // RFC 2045 token grammar for `type/subtype` with optional `; name=value` parameters. Strict on purpose:
    // "pdf" or a parameter without a value passed the old guard, got queued, and threw inside MimeKit at every
    // send until the message dead-lettered — with no error at call time.
    private static readonly System.Text.RegularExpressions.Regex MediaTypeShape = new(
        @"^[A-Za-z0-9!#$&^_.+-]+/[A-Za-z0-9!#$&^_.+-]+(?:\s*;\s*[A-Za-z0-9!#$&^_.+-]+=(?:""[^""]*""|[^\s;""=]+))*\s*$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool IsMediaType(string? mediaType) => !string.IsNullOrWhiteSpace(mediaType) && MediaTypeShape.IsMatch(mediaType);

    /// <summary>
    /// Guard every <see cref="IEmailSender"/> runs before queueing or sending. Throws
    /// <see cref="ArgumentException"/> (<c>ParamName = "attachments"</c>) for a null entry, a file name that is
    /// not a safe base name, a media type that is not <c>type/subtype</c>, missing content, more than
    /// <see cref="MaxCount"/> parts, or a combined raw size over <see cref="MaxTotalBytes"/>. Inline images count
    /// toward both caps and get the same media-type check. A null or empty list is valid.
    /// </summary>
    public static void Validate(IReadOnlyList<EmailAttachment>? attachments, IReadOnlyList<EmailInlineImage>? inlineImages)
    {
        long total = 0;
        var parts = 0;
        if (attachments is not null)
        {
            for (var i = 0; i < attachments.Count; i++)
            {
                var a = attachments[i]
                    ?? throw new ArgumentException($"Attachment #{i} is null.", nameof(attachments));
                if (string.IsNullOrWhiteSpace(a.FileName) || a.SafeFileName.Length == 0)
                    throw new ArgumentException($"Attachment #{i} has no usable file name.", nameof(attachments));
                if (!IsMediaType(a.MediaType))
                    throw new ArgumentException($"Attachment '{a.SafeFileName}' has a media type that is not type/subtype: '{a.MediaType}'.", nameof(attachments));
                if (a.Content is null)
                    throw new ArgumentException($"Attachment '{a.SafeFileName}' has no content.", nameof(attachments));
                total += a.Content.LongLength;
                parts++;
            }
        }
        if (inlineImages is not null)
        {
            for (var i = 0; i < inlineImages.Count; i++)
            {
                var image = inlineImages[i]
                    ?? throw new ArgumentException($"Inline image #{i} is null.", nameof(attachments));
                if (!IsMediaType(image.MediaType))
                    throw new ArgumentException($"Inline image '{image.ContentId}' has a media type that is not type/subtype: '{image.MediaType}'.", nameof(attachments));
                if (image.Content is null)
                    throw new ArgumentException($"Inline image '{image.ContentId}' has no content.", nameof(attachments));
                total += image.Content.LongLength;
                parts++;
            }
        }

        if (parts > MaxCount)
            throw new ArgumentException($"Email carries {parts} parts (attachments + inline images), over the {MaxCount}-part limit.", nameof(attachments));
        if (total > MaxTotalBytes)
            throw new ArgumentException(
                $"Email attachments total {total} bytes, over the {MaxTotalBytes}-byte limit ({EncodedSize(MaxTotalBytes)} bytes once encoded, under the relay's {RelayLimitBytes}).", nameof(attachments));
    }

    public static void Validate(IReadOnlyList<EmailAttachment>? attachments) => Validate(attachments, null);
}

/// <summary>Thrown when an email could not be delivered to the SMTP server (timeout, connect/auth/send failure).</summary>
public sealed class EmailSendException(string message, Exception innerException)
    : Exception(message, innerException);
