using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Email;
using Vuelto.Infrastructure.Outbox;

namespace Vuelto.Api.Tests.Outbox;

/// <summary>
/// Proves the email migration in JOBS-1: the app-facing <see cref="IEmailSender"/> enqueues onto the
/// outbox instead of sending inline, and <see cref="EmailOutboxHandler"/> performs the real send when
/// the dispatcher delivers it. Together these keep existing call sites unchanged while making mail
/// reliable (ADR-007). JOBS-4 adds file attachments to the same path.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OutboxEmailSenderTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task SendAsync_EnqueuesEmailMessage_RatherThanSendingInline()
    {
        await using (var db = Fixture.CreateContext())
        {
            var sender = new OutboxEmailSender(new EfOutbox(db, TimeProvider.System), db);
            await sender.SendAsync("a@b.com", "Subject", "<p>hi</p>");
        }

        await using var read = Fixture.CreateContext();
        var msg = await read.Set<OutboxMessage>().SingleAsync();
        Assert.Equal(OutboxEmailSender.MessageType, msg.Type);
        Assert.Equal(OutboxStatus.Pending, msg.Status);

        var payload = JsonSerializer.Deserialize<EmailOutboxPayload>(msg.Payload)!;
        Assert.Equal("a@b.com", payload.To);
        Assert.Equal("Subject", payload.Subject);
        Assert.Equal("<p>hi</p>", payload.HtmlBody);
        Assert.Null(payload.Attachments);
    }

    [Fact]
    public async Task SendAsync_WithAttachment_PersistsItInThePayload()
    {
        var pdf = new EmailAttachment("report.pdf", [0x25, 0x50, 0x44, 0x46, 0x00, 0xFF], "application/pdf");
        await using (var db = Fixture.CreateContext())
        {
            var sender = new OutboxEmailSender(new EfOutbox(db, TimeProvider.System), db);
            await sender.SendAsync("a@b.com", "Report", "<p>attached</p>", attachments: [pdf]);
        }

        await using var read = Fixture.CreateContext();
        var msg = await read.Set<OutboxMessage>().SingleAsync();
        var payload = JsonSerializer.Deserialize<EmailOutboxPayload>(msg.Payload)!;
        var att = Assert.Single(payload.Attachments!);
        Assert.Equal("report.pdf", att.FileName);
        Assert.Equal("application/pdf", att.MediaType);
        Assert.Equal(pdf.Content, att.Content);
    }

    [Fact]
    public async Task SendAsync_AttachmentsExactlyAtTheLimit_AreEnqueued()
    {
        var half = EmailAttachment.MaxTotalBytes / 2;
        await using (var db = Fixture.CreateContext())
        {
            var sender = new OutboxEmailSender(new EfOutbox(db, TimeProvider.System), db);
            await sender.SendAsync("a@b.com", "Big", "<p/>", attachments:
            [
                new EmailAttachment("a.pdf", new byte[half], "application/pdf"),
                new EmailAttachment("b.pdf", new byte[EmailAttachment.MaxTotalBytes - half], "application/pdf"),
            ]);
        }

        await using var read = Fixture.CreateContext();
        Assert.Equal(1, await read.Set<OutboxMessage>().CountAsync());
    }

    [Fact]
    public async Task SendAsync_AttachmentsOverTheLimit_AreRejected_AndNothingIsEnqueued()
    {
        await using (var db = Fixture.CreateContext())
        {
            var sender = new OutboxEmailSender(new EfOutbox(db, TimeProvider.System), db);
            var ex = await Assert.ThrowsAsync<ArgumentException>(() => sender.SendAsync("a@b.com", "Too big", "<p/>",
                attachments:
                [
                    new EmailAttachment("a.pdf", new byte[EmailAttachment.MaxTotalBytes], "application/pdf"),
                    new EmailAttachment("b.pdf", new byte[1], "application/pdf"),
                ]));
            Assert.Equal("attachments", ex.ParamName);
        }

        await using var read = Fixture.CreateContext();
        Assert.Equal(0, await read.Set<OutboxMessage>().CountAsync());
    }

    [Theory]
    [InlineData("", "application/pdf")]
    [InlineData("   ", "application/pdf")]
    [InlineData("report.pdf", "")]
    [InlineData("report.pdf", " ")]
    public async Task SendAsync_AttachmentWithBlankNameOrMediaType_IsRejected_AndNothingIsEnqueued(
        string fileName, string mediaType)
    {
        await using (var db = Fixture.CreateContext())
        {
            var sender = new OutboxEmailSender(new EfOutbox(db, TimeProvider.System), db);
            await Assert.ThrowsAsync<ArgumentException>(() => sender.SendAsync("a@b.com", "Bad", "<p/>",
                attachments: [new EmailAttachment(fileName, [1, 2, 3], mediaType)]));
        }

        await using var read = Fixture.CreateContext();
        Assert.Equal(0, await read.Set<OutboxMessage>().CountAsync());
    }
}

/// <summary>Pure unit tests (no DB): payload shape + the handler's replay of it.</summary>
public class EmailOutboxHandlerTests
{
    [Fact]
    public async Task HandleAsync_InvokesUnderlyingSender_WithDeserializedArgs()
    {
        var recording = new RecordingEmailSender();
        var handler = new EmailOutboxHandler(recording);
        var payload = JsonSerializer.Serialize(new EmailOutboxPayload("x@y.com", "Hi", "<b>b</b>", null));

        await handler.HandleAsync(new OutboxMessage { Type = "email", Payload = payload });

        var sent = Assert.Single(recording.Sent);
        Assert.Equal("x@y.com", sent.To);
        Assert.Equal("Hi", sent.Subject);
        Assert.Equal("<b>b</b>", sent.Body);
        Assert.Null(sent.Attachments);
    }

    [Fact]
    public async Task HandleAsync_ForwardsAttachments_ToTheUnderlyingSender()
    {
        var recording = new RecordingEmailSender();
        var handler = new EmailOutboxHandler(recording);
        var payload = JsonSerializer.Serialize(new EmailOutboxPayload("x@y.com", "Hi", "<b>b</b>", null,
            [new EmailAttachment("r.pdf", [9, 8, 7], "application/pdf")]));

        await handler.HandleAsync(new OutboxMessage { Type = "email", Payload = payload });

        var att = Assert.Single(Assert.Single(recording.Sent).Attachments!);
        Assert.Equal("r.pdf", att.FileName);
        Assert.Equal("application/pdf", att.MediaType);
        Assert.Equal(new byte[] { 9, 8, 7 }, att.Content);
    }

    [Fact]
    public void Payload_RoundTrips_AttachmentBytesAsBase64()
    {
        var bytes = new byte[] { 0x00, 0x01, 0xFE, 0xFF, 0x25, 0x50 };
        var json = JsonSerializer.Serialize(new EmailOutboxPayload("x@y.com", "S", "<p/>", null,
            [new EmailAttachment("r.pdf", bytes, "application/pdf")]));

        Assert.Contains($"\"Content\":\"{Convert.ToBase64String(bytes)}\"", json);
        var back = JsonSerializer.Deserialize<EmailOutboxPayload>(json)!;
        Assert.Equal(bytes, Assert.Single(back.Attachments!).Content);
    }

    [Fact]
    public async Task HandleAsync_PayloadEnqueuedBeforeAttachmentsExisted_StillDeserializesAndSends()
    {
        // A message written by the previous build sits in the outbox across the deploy: it has no
        // "Attachments" property at all. It must still replay (as a mail with no attachments).
        const string oldShape =
            """{"To":"x@y.com","Subject":"Old","HtmlBody":"<p>old</p>","InlineImages":[{"ContentId":"logo","FileName":"logo.png","Content":"AQID","MediaType":"image/png"}]}""";

        var recording = new RecordingEmailSender();
        await new EmailOutboxHandler(recording).HandleAsync(new OutboxMessage { Type = "email", Payload = oldShape });

        var sent = Assert.Single(recording.Sent);
        Assert.Equal("Old", sent.Subject);
        Assert.Null(sent.Attachments);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(sent.InlineImages!).Content);
    }
}

internal sealed class RecordingEmailSender : IEmailSender
{
    public List<(string To, string Subject, string Body, IReadOnlyList<EmailInlineImage>? InlineImages,
        IReadOnlyList<EmailAttachment>? Attachments)> Sent { get; } = [];

    public Task SendAsync(string to, string subject, string htmlBody,
        IReadOnlyList<EmailInlineImage>? inlineImages = null, IReadOnlyList<EmailAttachment>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        Sent.Add((to, subject, htmlBody, inlineImages, attachments));
        return Task.CompletedTask;
    }
}
