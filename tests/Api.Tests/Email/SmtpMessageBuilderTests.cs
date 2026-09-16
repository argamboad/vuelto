using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using Vuelto.Core.Abstractions;
using Vuelto.Infrastructure.Email;

namespace Vuelto.Api.Tests.Email;

/// <summary>
/// JOBS-4: the MIME message <see cref="SmtpEmailSender"/> hands to MailKit carries each attachment as
/// a real attachment part (right filename + media type) next to the CID inline images. Tested on the
/// message builder, so no SMTP server is needed.
/// </summary>
public class SmtpMessageBuilderTests
{
    private static readonly SmtpSettings Settings = new() { Host = "localhost", FromAddress = "noreply@test.local", FromName = "Test" };

    [Fact]
    public void BuildMessage_AddsAttachmentPart_WithFileNameAndMediaType()
    {
        var pdf = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D };
        var message = SmtpEmailSender.BuildMessage(Settings, "to@test.local", "Report", "<p>hi</p>",
            inlineImages: null, attachments: [new EmailAttachment("report.pdf", pdf, "application/pdf")]);

        var part = Assert.IsType<MimePart>(Assert.Single(message.Attachments));
        Assert.Equal("report.pdf", part.FileName);
        Assert.Equal("application/pdf", part.ContentType.MimeType);
        Assert.True(part.IsAttachment);

        using var ms = new MemoryStream();
        Assert.NotNull(part.Content);
        part.Content.DecodeTo(ms);
        Assert.Equal(pdf, ms.ToArray());
    }

    [Fact]
    public void BuildMessage_KeepsInlineImagesAsLinkedResources_AlongsideAttachments()
    {
        var message = SmtpEmailSender.BuildMessage(Settings, "to@test.local", "S", "<img src=\"cid:logo\">",
            inlineImages: [new EmailInlineImage("logo", "logo.png", [1, 2, 3], "image/png")],
            attachments: [new EmailAttachment("a.csv", [4, 5], "text/csv")]);

        // text/* attachments come back as TextPart (a MimePart subclass).
        var attachment = Assert.IsAssignableFrom<MimePart>(Assert.Single(message.Attachments));
        Assert.Equal("a.csv", attachment.FileName);
        Assert.Equal("text/csv", attachment.ContentType.MimeType);

        var logo = message.BodyParts.OfType<MimePart>().Single(p => p.ContentId == "logo");
        Assert.False(logo.IsAttachment);
        Assert.Equal("image/png", logo.ContentType.MimeType);

        Assert.Equal("S", message.Subject);
        Assert.Equal("to@test.local", message.To.Mailboxes.Single().Address);
        Assert.Equal("noreply@test.local", message.From.Mailboxes.Single().Address);
    }

    [Fact]
    public void BuildMessage_NoAttachments_HasNoAttachmentParts()
    {
        var message = SmtpEmailSender.BuildMessage(Settings, "to@test.local", "S", "<p/>", null, null);
        Assert.Empty(message.Attachments);
        Assert.Equal("<p/>", message.HtmlBody);
    }

    [Fact]
    public async Task SendAsync_OversizeAttachments_RejectedBeforeAnyConnectAttempt()
    {
        // Host points nowhere: if the guard didn't fire first, this would surface as EmailSendException.
        var sender = new SmtpEmailSender(Options.Create(new SmtpSettings { Host = "invalid.invalid", FromAddress = "a@b.c" }),
            NullLogger<SmtpEmailSender>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => sender.SendAsync("to@test.local", "S", "<p/>",
            attachments: [new EmailAttachment("big.pdf", new byte[EmailAttachment.MaxTotalBytes + 1], "application/pdf")]));
    }

    [Fact]
    public async Task SendAsync_BlankAttachmentName_RejectedBeforeAnyConnectAttempt()
    {
        var sender = new SmtpEmailSender(Options.Create(new SmtpSettings { Host = "invalid.invalid", FromAddress = "a@b.c" }),
            NullLogger<SmtpEmailSender>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => sender.SendAsync("to@test.local", "S", "<p/>",
            attachments: [new EmailAttachment("", [1], "application/pdf")]));
    }
}
