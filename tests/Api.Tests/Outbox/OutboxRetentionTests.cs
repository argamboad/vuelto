using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Email;
using Vuelto.Infrastructure.Outbox;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Api.Tests.Outbox;

/// <summary>
/// v4 audit H7 (T23, JOBS-2 + C14, R90) and decision #6: an outbox row is a delivery instruction, not an archive.
/// It used to keep its whole payload after delivery — recipient, body, up to ~13 MiB of base64 attachment — with
/// nothing ever purging it, a dead-lettered row had no terminal timestamp, and erasing an account left the mail
/// still addressed to it. Now a finished row (sent or dead) keeps only <c>{}</c>, dead rows are stamped, a
/// scheduled job deletes finished rows after <c>Outbox:RetentionDays</c> (30), and erasure removes the user's
/// pending mail. The one exception is a handler that declares its payload a record — the platform broadcast, the
/// only attribution of who sent a platform-wide announcement — which is neither cleared nor purged.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OutboxRetentionTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Processor_ClearsThePayload_WhenSent()
    {
        var id = await SeedAsync(Pending(OutboxEmailSender.MessageType));

        await using (var db = Fixture.CreateContext())
            Assert.True(await Processor(db, new RecordingHandler(OutboxEmailSender.MessageType)).ProcessNextAsync());

        var row = await ReadAsync(id);
        Assert.Equal(OutboxStatus.Sent, row.Status);
        Assert.Equal(OutboxMessage.ClearedPayload, row.Payload);
        Assert.Equal(Now, row.ProcessedAt);
    }

    [Fact]
    public async Task Processor_ClearsThePayload_AndStampsTheTime_WhenDeadLettered()
    {
        var id = await SeedAsync(Pending(OutboxEmailSender.MessageType));

        await using (var db = Fixture.CreateContext())
            await Processor(db, new ThrowingHandler(OutboxEmailSender.MessageType), new OutboxOptions { MaxAttempts = 1 }).ProcessNextAsync();

        var row = await ReadAsync(id);
        Assert.Equal(OutboxStatus.DeadLettered, row.Status);
        Assert.Equal(OutboxMessage.ClearedPayload, row.Payload);
        Assert.Equal(Now, row.ProcessedAt); // a dead row has a terminal time too, so retention can age it
        Assert.Contains("handler boom", row.LastError);
    }

    [Fact]
    public async Task Processor_LeavesARetryingRowsPayload_ForItsNextAttempt()
    {
        var id = await SeedAsync(Pending(OutboxEmailSender.MessageType));

        await using (var db = Fixture.CreateContext())
            await Processor(db, new ThrowingHandler(OutboxEmailSender.MessageType)).ProcessNextAsync(); // 1 of 5

        var row = await ReadAsync(id);
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Contains("member@x.com", row.Payload);
        Assert.Null(row.ProcessedAt);
    }

    [Fact]
    public async Task Processor_KeepsThePayload_OfAHandlerThatDeclaresItARecord()
    {
        var id = await SeedAsync(Pending("record"));

        await using (var db = Fixture.CreateContext())
            await Processor(db, new KeepingHandler("record")).ProcessNextAsync();

        var row = await ReadAsync(id);
        Assert.Equal(OutboxStatus.Sent, row.Status);
        Assert.Contains("member@x.com", row.Payload);
    }

    [Fact]
    public async Task RetentionJob_DeletesFinishedRowsPastRetention_ClearsLeftovers_KeepsTheRest()
    {
        var sentOld = await SeedAsync(Finished(OutboxStatus.Sent, processed: Now.AddDays(-31)));
        var deadLegacy = await SeedAsync(Finished(OutboxStatus.DeadLettered, processed: null, created: Now.AddDays(-40)));
        var sentYoungUncleared = await SeedAsync(Finished(OutboxStatus.Sent, processed: Now.AddDays(-1)));
        var pendingOld = await SeedAsync(Pending(OutboxEmailSender.MessageType, created: Now.AddDays(-40)));
        var recordOld = await SeedAsync(Finished(OutboxStatus.Sent, processed: Now.AddDays(-400), type: "record"));

        await using (var db = Fixture.CreateContext())
            await new OutboxRetentionJob(db, [new RecordingHandler(OutboxEmailSender.MessageType), new KeepingHandler("record")],
                new OutboxOptions { RetentionDays = 30 }, new FakeTimeProvider(Now)).RunAsync();

        await using var read = Fixture.CreateContext();
        var left = await read.Set<OutboxMessage>().ToDictionaryAsync(m => m.Id);
        Assert.False(left.ContainsKey(sentOld));
        Assert.False(left.ContainsKey(deadLegacy)); // finished before dead rows were stamped: aged by CreatedAt
        Assert.Equal(OutboxMessage.ClearedPayload, left[sentYoungUncleared].Payload); // finished before clearing existed
        Assert.Contains("member@x.com", left[pendingOld].Payload); // pending is never touched
        Assert.Contains("member@x.com", left[recordOld].Payload);  // a record is kept, whole
    }

    [Fact]
    public async Task Erasure_RemovesTheUsersPendingMail_OtherMailIntact()
    {
        var userId = Guid.CreateVersion7();
        await using (var db = Fixture.CreateContext())
        {
            db.Users.Add(new User { Id = userId, Email = "bob+bank@x.com" });
            await db.SaveChangesAsync();
        }
        var attachment = new EmailAttachment("statement.pdf", [0x25, 0x50, 0x44, 0x46], "application/pdf");
        var toUser = await SeedAsync(Email("Bob+Bank@X.com", [attachment])); // address as the sender typed it
        var toOther = await SeedAsync(Email("alice@x.com"));
        var mentionsUser = await SeedAsync(Email("alice@x.com", subject: "bob+bank@x.com")); // the address, but not as recipient

        await using (var db = Fixture.CreateContext())
            await new OutboxUserDataContributor(db).WipeAsync(userId);

        await using var read = Fixture.CreateContext();
        var left = await read.Set<OutboxMessage>().Select(m => m.Id).ToListAsync();
        Assert.DoesNotContain(toUser, left);  // gone, attachment bytes with it
        Assert.Contains(toOther, left);
        Assert.Contains(mentionsUser, left);  // only the recipient field counts, not another field holding the address
    }

    [Fact]
    public void EveryOutboxHandler_DeclaresWhetherItsPayloadIsARecord()
    {
        // Only the broadcast keeps its payload once done: it is the one attribution of a platform-wide announcement
        // (the audit trail is per tenant), and it carries the staff id and the announcement copy, no personal data.
        var expected = new Dictionary<string, bool>
        {
            [OutboxEmailSender.MessageType] = false,
            [Vuelto.Infrastructure.Webhooks.WebhookOutboxHandler.MessageType] = false, // the delivery log keeps the body
            [Vuelto.Infrastructure.Billing.BillingCancelOutboxHandler.MessageType] = false,
            [Vuelto.Api.Services.AdminBroadcastOutboxHandler.MessageType] = true,
        };

        var declared = OutboxTenancyTests.AllHandlers().ToDictionary(h => h.Type, h => h.KeepsPayloadWhenDone);
        Assert.Equal(expected.OrderBy(kv => kv.Key), declared.OrderBy(kv => kv.Key));
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData("7", 7)]
    public void RetentionDays_ComesFromConfiguration_DefaultingTo30(string? configured, int expected)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configured is null ? [] : [new("Outbox:RetentionDays", configured)]).Build();
        Assert.Equal(expected, OutboxOptions.FromConfiguration(config).RetentionDays);
    }

    [Fact]
    public void RetentionDays_BelowOne_FailsAtStartup()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([new("Outbox:RetentionDays", "0")]).Build();
        Assert.Throws<InvalidOperationException>(() => OutboxOptions.FromConfiguration(config));
    }

    // --- helpers ---

    private OutboxProcessor Processor(AppDbContext db, IOutboxHandler handler, OutboxOptions? options = null) =>
        new(db, [handler], new FakeTimeProvider(Now), options ?? new OutboxOptions(), NullLogger<OutboxProcessor>.Instance);

    private async Task<Guid> SeedAsync(OutboxMessage row)
    {
        await using var db = Fixture.CreateContext();
        db.Set<OutboxMessage>().Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private async Task<OutboxMessage> ReadAsync(Guid id)
    {
        await using var read = Fixture.CreateContext();
        return await read.Set<OutboxMessage>().SingleAsync(m => m.Id == id);
    }

    private static OutboxMessage Pending(string type, DateTimeOffset? created = null) => new()
    {
        Type = type,
        Payload = JsonSerializer.Serialize(new EmailOutboxPayload("member@x.com", "s", "<p>b</p>", null)),
        CreatedAt = created ?? Now.AddMinutes(-1),
        NextAttemptAt = created ?? Now.AddMinutes(-1),
    };

    private static OutboxMessage Finished(string status, DateTimeOffset? processed, DateTimeOffset? created = null, string? type = null)
    {
        var row = Pending(type ?? OutboxEmailSender.MessageType, created ?? processed);
        row.Status = status;
        row.ProcessedAt = processed;
        return row;
    }

    private static OutboxMessage Email(string to, IReadOnlyList<EmailAttachment>? attachments = null, string subject = "s")
    {
        var row = Pending(OutboxEmailSender.MessageType);
        row.Payload = JsonSerializer.Serialize(new EmailOutboxPayload(to, subject, "<p>b</p>", null, attachments));
        return row;
    }
}

/// <summary>A handler whose payload is a record (<see cref="IOutboxHandler.KeepsPayloadWhenDone"/>).</summary>
internal sealed class KeepingHandler(string type) : IOutboxHandler
{
    public string Type => type;
    public bool DissolvesWithItsTenant => false;
    public bool KeepsPayloadWhenDone => true;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
