using Vuelto.Core.Abstractions;

namespace Vuelto.Core.Tests;

/// <summary>
/// JOBS-4 boundary tests for the attachment guard every <see cref="IEmailSender"/> runs: the total-size
/// cap (Brevo's 10 MB) is inclusive, and a blank file name or media type is refused.
/// </summary>
public class EmailAttachmentTests
{
    [Fact]
    public void MaxTotalBytes_IsTenMebibytes() => Assert.Equal(10 * 1024 * 1024, EmailAttachment.MaxTotalBytes);

    [Fact]
    public void Validate_NullOrEmpty_IsAccepted()
    {
        EmailAttachment.Validate(null);
        EmailAttachment.Validate([]);
    }

    [Fact]
    public void Validate_TotalExactlyAtTheLimit_IsAccepted() =>
        EmailAttachment.Validate(
        [
            new EmailAttachment("a.pdf", new byte[EmailAttachment.MaxTotalBytes - 1], "application/pdf"),
            new EmailAttachment("b.pdf", new byte[1], "application/pdf"),
        ]);

    [Fact]
    public void Validate_TotalOverTheLimit_IsRejected_WithAClearMessage()
    {
        var ex = Assert.Throws<ArgumentException>(() => EmailAttachment.Validate(
        [
            new EmailAttachment("a.pdf", new byte[EmailAttachment.MaxTotalBytes], "application/pdf"),
            new EmailAttachment("b.pdf", new byte[1], "application/pdf"),
        ]));
        Assert.Equal("attachments", ex.ParamName);
        Assert.Contains("10485761", ex.Message);
        Assert.Contains("10485760", ex.Message);
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

    [Fact]
    public void Validate_NullAttachmentOrContent_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => EmailAttachment.Validate([null!]));
        Assert.Throws<ArgumentException>(() => EmailAttachment.Validate([new EmailAttachment("r.pdf", null!, "application/pdf")]));
    }
}
