using Vuelto.Core.Abstractions;

namespace Vuelto.Core.Tests;

/// <summary>
/// JOBS-4 boundary tests for the attachment guard every <see cref="IEmailSender"/> runs — strict since v4 T40
/// (JOBS-3/4, LB-JOBS-4, R92/R132): a media type that is not <c>type/subtype</c>, a file name that is not a safe
/// base name, too many parts, or a total the relay would refuse once base64-encoded is refused at call time,
/// not five send attempts later in the outbox.
/// </summary>
public class EmailAttachmentTests
{
    private static EmailAttachment Pdf(string name = "r.pdf", int bytes = 1) => new(name, new byte[bytes], "application/pdf");

    [Fact]
    public void MaxTotalBytes_LeavesRoomForBase64_UnderTheRelaysTenMiB()
    {
        // The relay (Brevo) caps the MESSAGE at 10 MiB. Base64 is 4/3 the raw size, plus a CRLF every 76 chars:
        // 10 MiB of raw bytes encodes to ~13.7 MiB and is refused after five retries. The raw cap is what fits.
        Assert.Equal(7 * 1024 * 1024, EmailAttachment.MaxTotalBytes);
        Assert.Equal(10 * 1024 * 1024, EmailAttachment.RelayLimitBytes);
        Assert.True(EmailAttachment.EncodedSize(EmailAttachment.MaxTotalBytes) < EmailAttachment.RelayLimitBytes - 256 * 1024,
            "the encoded maximum must leave headroom for the body and headers");
    }

    [Fact]
    public void Validate_NullOrEmpty_IsAccepted()
    {
        EmailAttachment.Validate(null);
        EmailAttachment.Validate([]);
        EmailAttachment.Validate([], []);
    }

    [Fact]
    public void Validate_TotalExactlyAtTheLimit_IsAccepted() =>
        EmailAttachment.Validate([Pdf("a.pdf", EmailAttachment.MaxTotalBytes - 1), Pdf("b.pdf", 1)]);

    [Fact]
    public void Validate_TotalOverTheLimit_IsRejected_WithAClearMessage()
    {
        var ex = Assert.Throws<ArgumentException>(() => EmailAttachment.Validate([Pdf("a.pdf", EmailAttachment.MaxTotalBytes), Pdf("b.pdf", 1)]));
        Assert.Equal("attachments", ex.ParamName);
        Assert.Contains((EmailAttachment.MaxTotalBytes + 1).ToString(), ex.Message);
        Assert.Contains(EmailAttachment.MaxTotalBytes.ToString(), ex.Message);
    }

    [Fact]
    public void Validate_CountsInlineImages_TowardTheSizeCap()
    {
        var images = new[] { new EmailInlineImage("logo", "logo.png", new byte[EmailAttachment.MaxTotalBytes / 2 + 1], "image/png") };

        EmailAttachment.Validate([Pdf("a.pdf", EmailAttachment.MaxTotalBytes / 2 - 1)], images);
        Assert.Throws<ArgumentException>(() => EmailAttachment.Validate([Pdf("a.pdf", EmailAttachment.MaxTotalBytes / 2)], images));
    }

    [Theory]
    [InlineData("", "application/pdf")]
    [InlineData("  ", "application/pdf")]
    [InlineData(null, "application/pdf")]
    [InlineData("r.pdf", "")]
    [InlineData("r.pdf", "\t")]
    [InlineData("r.pdf", null)]
    public void Validate_BlankFileNameOrMediaType_IsRejected(string? fileName, string? mediaType)
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            EmailAttachment.Validate([new EmailAttachment(fileName!, [1], mediaType!)]));
        Assert.Equal("attachments", ex.ParamName);
    }

    [Theory]
    [InlineData("pdf", false)]                          // no subtype: MimeKit throws at send time, five times, then dead-letters
    [InlineData("application/", false)]
    [InlineData("/pdf", false)]
    [InlineData("application/pdf; charset=", false)]   // a broken parameter
    [InlineData("application pdf", false)]
    [InlineData("application/pdf", true)]
    [InlineData("image/png", true)]
    [InlineData("text/plain; charset=utf-8", true)]
    [InlineData("application/vnd.ms-excel", true)]
    public void Validate_MediaType_MustBeTypeSlashSubtype(string mediaType, bool valid)
    {
        var attachments = new[] { new EmailAttachment("r.bin", [1], mediaType) };

        if (valid) EmailAttachment.Validate(attachments);
        else Assert.Throws<ArgumentException>(() => EmailAttachment.Validate(attachments));
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd")]          // never a path: the MIME header carried it verbatim
    [InlineData(@"C:\Users\me\report.pdf", "report.pdf")] // a backslash is a separator too
    [InlineData("re\u0000port\n.pdf", "report.pdf")]    // control characters dropped
    [InlineData("  report.pdf  ", "report.pdf")]
    [InlineData("report.pdf", "report.pdf")]
    public void SafeFileName_IsABaseName_WithoutControlCharacters(string given, string expected)
    {
        Assert.Equal(expected, new EmailAttachment(given, [1], "application/pdf").SafeFileName);
    }

    [Fact]
    public void SafeFileName_IsAtMost255Characters()
    {
        var name = new string('a', 300) + ".pdf";

        Assert.Equal(255, new EmailAttachment(name, [1], "application/pdf").SafeFileName.Length);
    }

    [Theory]
    [InlineData("../")]      // nothing left once the path is stripped
    [InlineData("\u0001\u0002")]
    public void Validate_AFileNameThatReducesToNothing_IsRejected(string given)
    {
        Assert.Throws<ArgumentException>(() => EmailAttachment.Validate([new EmailAttachment(given, [1], "application/pdf")]));
    }

    [Fact]
    public void Validate_TooManyParts_IsRejected_InlineImagesIncluded()
    {
        var atMost = Enumerable.Range(0, EmailAttachment.MaxCount).Select(i => Pdf($"{i}.pdf")).ToArray();
        EmailAttachment.Validate(atMost);

        Assert.Throws<ArgumentException>(() => EmailAttachment.Validate([.. atMost, Pdf("one-more.pdf")]));
        Assert.Throws<ArgumentException>(() => EmailAttachment.Validate(atMost, [new EmailInlineImage("logo", "logo.png", [1], "image/png")]));
    }

    [Fact]
    public void Validate_NullAttachmentOrContent_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => EmailAttachment.Validate([null!]));
        Assert.Throws<ArgumentException>(() => EmailAttachment.Validate([new EmailAttachment("r.pdf", null!, "application/pdf")]));
    }
}
