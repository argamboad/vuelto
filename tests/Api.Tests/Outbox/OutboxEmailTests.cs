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
/// reliable (ADR-007).
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
    }
}

/// <summary>Pure unit test (no DB): the handler deserializes the payload and calls the real sender.</summary>
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
    }
}

internal sealed class RecordingEmailSender : IEmailSender
{
    public List<(string To, string Subject, string Body)> Sent { get; } = [];

    public Task SendAsync(string to, string subject, string htmlBody,
        IReadOnlyList<EmailInlineImage>? inlineImages = null, CancellationToken cancellationToken = default)
    {
        Sent.Add((to, subject, htmlBody));
        return Task.CompletedTask;
    }
}
