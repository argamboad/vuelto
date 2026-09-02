using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Outbox;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Api.Tests.Outbox;

/// <summary>
/// Drives JOBS-1 (ADR-007): the outbox makes side effects atomic with the data change and the
/// dispatcher delivers them reliably (sent, retried, or dead-lettered). Runs against real Postgres
/// because the claim uses <c>FOR UPDATE SKIP LOCKED</c> and the atomicity test needs real
/// transactions — neither is modelled by the EF in-memory provider.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OutboxProcessorTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    // --- atomicity: an enqueued effect commits with the business change, or not at all ---

    [Fact]
    public async Task Enqueue_RolledBack_PersistsNeitherMessageNorBusinessChange()
    {
        var tenant = Guid.CreateVersion7();

        await using (var db = Fixture.CreateContext(tenant))
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            db.Set<TestWidget>().Add(new TestWidget { Name = "business change", TenantId = tenant });
            await new EfOutbox(db, TimeProvider.System).EnqueueAsync("email", "{}");
            await db.SaveChangesAsync();
            // tx disposed without CommitAsync -> rollback
        }

        await using var read = Fixture.CreateContext();
        Assert.Empty(await read.Set<OutboxMessage>().ToListAsync());
        Assert.Empty(await read.Set<TestWidget>().IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Enqueue_Committed_PersistsPendingMessage()
    {
        await using (var db = Fixture.CreateContext())
        {
            await new EfOutbox(db, TimeProvider.System).EnqueueAsync("email", "payload");
            await db.SaveChangesAsync();
        }

        await using var read = Fixture.CreateContext();
        var msg = await read.Set<OutboxMessage>().SingleAsync();
        Assert.Equal("email", msg.Type);
        Assert.Equal(OutboxStatus.Pending, msg.Status);
        Assert.Equal(0, msg.AttemptCount);
    }

    // --- dispatch ---

    [Fact]
    public async Task ProcessDue_DeliversDueMessageToHandler_AndMarksSent()
    {
        await SeedAsync("recording", "hi");
        var handler = new RecordingHandler("recording");

        await using (var db = Fixture.CreateContext())
            Assert.Equal(1, await NewProcessor(db, handler).ProcessDueAsync());

        Assert.Single(handler.Handled);

        await using var read = Fixture.CreateContext();
        var msg = await read.Set<OutboxMessage>().SingleAsync();
        Assert.Equal(OutboxStatus.Sent, msg.Status);
        Assert.NotNull(msg.ProcessedAt);
    }

    [Fact]
    public async Task ProcessDue_SkipsMessagesNotYetDue()
    {
        await SeedAsync("recording", "later", notBefore: TimeSpan.FromMinutes(10));
        var handler = new RecordingHandler("recording");

        await using var db = Fixture.CreateContext();
        Assert.Equal(0, await NewProcessor(db, handler).ProcessDueAsync());
        Assert.Empty(handler.Handled);
    }

    // --- failure handling: retry with backoff, then dead-letter at the attempt cap ---

    [Fact]
    public async Task ProcessDue_FailingHandler_RetriesThenDeadLetters()
    {
        await SeedAsync("boom", "x");
        var handler = new ThrowingHandler("boom");
        // Backoff zero keeps the message immediately due, so a single pass exhausts the attempts.
        var options = new OutboxOptions { MaxAttempts = 3, BackoffBase = TimeSpan.Zero };

        await using (var db = Fixture.CreateContext())
            await NewProcessor(db, handler, options).ProcessDueAsync();

        await using var read = Fixture.CreateContext();
        var msg = await read.Set<OutboxMessage>().SingleAsync();
        Assert.Equal(OutboxStatus.DeadLettered, msg.Status);
        Assert.Equal(3, msg.AttemptCount);
        Assert.NotNull(msg.LastError);
    }

    // --- helpers ---

    [Fact]
    public async Task ProcessDue_HandlerStagesARowThatFaultsAtCommit_StillAdvancesAttempt_AndDeadLetters()
    {
        // v3 LB-BILL-2: the handler succeeds but stages a row that only faults when the processor COMMITS
        // (a NOT NULL violation here; equivalently a transient commit-time disconnect). The attempt must
        // still be accounted for and the message must eventually DEAD-LETTER — not stay Pending and re-run
        // the side effect every pass forever.
        await SeedAsync("poison", "x");
        var options = new OutboxOptions { MaxAttempts = 2, BackoffBase = TimeSpan.Zero };

        await using (var db = Fixture.CreateContext())
        {
            var handler = new StagesBadRowHandler("poison", db);
            await NewProcessor(db, handler, options).ProcessDueAsync();
        }

        await using var read = Fixture.CreateContext();
        var msg = await read.Set<OutboxMessage>().SingleAsync(m => m.Type == "poison");
        Assert.Equal(OutboxStatus.DeadLettered, msg.Status); // OLD code: throws / stays Pending forever
        Assert.Equal(2, msg.AttemptCount);
        Assert.NotNull(msg.LastError);
    }

    [Fact]
    public async Task ProcessDue_FailingHandler_BackoffGrowsExponentially()
    {
        // v3 TB-BILL backfill (T45b): the retry SCHEDULE, not just the retry count — the nth failure
        // must push NextAttemptAt out by Base * 2^(n-1), or a hot-failing handler hammers its
        // downstream at poll frequency instead of backing off.
        await SeedAsync("boom", "x");
        // The seed stamps NextAttemptAt with the REAL wall clock, so the fake clock must start ahead
        // of it for the message to be due on the first pass.
        var clock = new FakeTimeProvider(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = new OutboxOptions { MaxAttempts = 5, BackoffBase = TimeSpan.FromSeconds(10) };
        var handler = new ThrowingHandler("boom");

        foreach (var expectedDelay in new[] { 10, 20, 40 }) // 10s * 2^(n-1) for n = 1, 2, 3
        {
            await using (var db = Fixture.CreateContext())
                Assert.Equal(1, await NewProcessor(db, handler, options, clock).ProcessDueAsync());

            await using var read = Fixture.CreateContext();
            var msg = await read.Set<OutboxMessage>().SingleAsync();
            Assert.Equal(OutboxStatus.Pending, msg.Status);
            Assert.Equal(clock.GetUtcNow() + TimeSpan.FromSeconds(expectedDelay), msg.NextAttemptAt);

            clock.Advance(TimeSpan.FromSeconds(expectedDelay)); // make it due for the next round
        }
    }

    [Fact]
    public async Task ProcessDue_MessageWithNoRegisteredHandler_RetriesThenDeadLetters()
    {
        // v3 TB-BILL backfill (T45b): a message whose type has NO registered handler (a renamed
        // constant, a handler dropped from DI) must follow the normal retry→dead-letter path with a
        // diagnosable error — not crash the poller or silently vanish.
        await SeedAsync("nobody-handles-this", "x");
        var options = new OutboxOptions { MaxAttempts = 2, BackoffBase = TimeSpan.Zero };

        await using (var db = Fixture.CreateContext())
            await NewProcessor(db, new RecordingHandler("some-other-type"), options).ProcessDueAsync();

        await using var read = Fixture.CreateContext();
        var msg = await read.Set<OutboxMessage>().SingleAsync();
        Assert.Equal(OutboxStatus.DeadLettered, msg.Status);
        Assert.Contains("No IOutboxHandler", msg.LastError);
    }

    [Fact]
    public async Task ProcessDue_TwoConcurrentPollers_DeliverEachMessageExactlyOnce()
    {
        // v3 TB-BILL backfill (T45b): two dispatcher instances polling the same table (two app
        // replicas, or the host overlapping a slow pass) must not double-deliver — the claim is
        // FOR UPDATE SKIP LOCKED, so every message is handled exactly once across both.
        const int messages = 6;
        for (var i = 0; i < messages; i++)
            await SeedAsync("recording", $"m{i}");

        var (handlerA, handlerB) = (new RecordingHandler("recording"), new RecordingHandler("recording"));
        async Task<int> DrainAsync(RecordingHandler handler)
        {
            var delivered = 0;
            await using var db = Fixture.CreateContext();
            var processor = NewProcessor(db, handler);
            int batch;
            while ((batch = await processor.ProcessDueAsync()) > 0) delivered += batch;
            return delivered;
        }

        var counts = await Task.WhenAll(DrainAsync(handlerA), DrainAsync(handlerB));

        Assert.Equal(messages, counts.Sum());                       // nothing lost, nothing doubled —
        Assert.Equal(messages, handlerA.Handled.Count + handlerB.Handled.Count);
        await using var read = Fixture.CreateContext();
        Assert.Equal(messages, await read.Set<OutboxMessage>().CountAsync(m => m.Status == OutboxStatus.Sent));
    }

    private async Task SeedAsync(string type, string payload, TimeSpan? notBefore = null)
    {
        await using var db = Fixture.CreateContext();
        var now = TimeProvider.System.GetUtcNow();
        db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Type = type,
            Payload = payload,
            Status = OutboxStatus.Pending,
            CreatedAt = now,
            NextAttemptAt = now + (notBefore ?? TimeSpan.Zero),
        });
        await db.SaveChangesAsync();
    }

    private static OutboxProcessor NewProcessor(AppDbContext db, IOutboxHandler handler, OutboxOptions? options = null, TimeProvider? clock = null) =>
        new(db, [handler], clock ?? TimeProvider.System, options ?? new OutboxOptions(), NullLogger<OutboxProcessor>.Instance);
}

internal sealed class RecordingHandler(string type) : IOutboxHandler
{
    public List<OutboxMessage> Handled { get; } = [];
    public string Type => type;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        Handled.Add(message);
        return Task.CompletedTask;
    }
}

internal sealed class ThrowingHandler(string type) : IOutboxHandler
{
    public string Type => type;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("handler boom");
}

/// <summary>Handler that SUCCEEDS but stages a row that only faults when the processor commits (a NOT NULL
/// violation on the required Payload column) — the LB-BILL-2 commit-time-failure case.</summary>
internal sealed class StagesBadRowHandler(string type, AppDbContext db) : IOutboxHandler
{
    public string Type => type;

    public Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        db.Set<OutboxMessage>().Add(new OutboxMessage
        {
            Type = "staged-bad",
            Payload = null!, // NOT NULL column → SaveChanges throws at commit time
            CreatedAt = DateTimeOffset.UtcNow,
            NextAttemptAt = DateTimeOffset.UtcNow,
        });
        return Task.CompletedTask;
    }
}
