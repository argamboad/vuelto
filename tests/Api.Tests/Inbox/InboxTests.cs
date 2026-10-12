using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Inbox;

namespace Vuelto.Api.Tests.Inbox;

/// <summary>
/// Drives JOBS-2 (ADR-007): the inbox gives inbound at-least-once deliveries exactly-once handling.
/// First delivery claims; duplicates no-op; concurrent duplicates claim once; and a claim made inside
/// a rolled-back transaction frees the key (so a redelivery re-processes). Real Postgres is required —
/// the dedup relies on a unique constraint + ON CONFLICT and the concurrency test needs real
/// connections/transactions.
/// </summary>
[Collection(PostgresCollection.Name)]
public class InboxTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task TryClaim_FirstDelivery_IsClaimedAndRecorded()
    {
        await using var db = Fixture.CreateContext();
        var inbox = new EfInbox(db, TimeProvider.System);

        Assert.True(await inbox.TryClaimAsync("stripe", "evt_1"));

        await using var read = Fixture.CreateContext();
        var row = await read.Set<InboxMessage>().SingleAsync();
        Assert.Equal("stripe", row.Source);
        Assert.Equal("evt_1", row.IdempotencyKey);
    }

    [Fact]
    public async Task TryClaim_DuplicateDelivery_IsNoOp()
    {
        await using var db = Fixture.CreateContext();
        var inbox = new EfInbox(db, TimeProvider.System);

        Assert.True(await inbox.TryClaimAsync("stripe", "evt_dup"));
        Assert.False(await inbox.TryClaimAsync("stripe", "evt_dup"));

        await using var read = Fixture.CreateContext();
        Assert.Equal(1, await read.Set<InboxMessage>().CountAsync());
    }

    [Fact]
    public async Task TryClaim_SameKeyDifferentSource_AreIndependent()
    {
        await using var db = Fixture.CreateContext();
        var inbox = new EfInbox(db, TimeProvider.System);

        Assert.True(await inbox.TryClaimAsync("stripe", "evt_x"));
        Assert.True(await inbox.TryClaimAsync("paddle", "evt_x"));

        await using var read = Fixture.CreateContext();
        Assert.Equal(2, await read.Set<InboxMessage>().CountAsync());
    }

    [Fact]
    public async Task TryClaim_ConcurrentDuplicates_ClaimExactlyOnce()
    {
        // Separate contexts = separate connections, so the two claims genuinely race.
        await using var db1 = Fixture.CreateContext();
        await using var db2 = Fixture.CreateContext();

        var results = await Task.WhenAll(
            new EfInbox(db1, TimeProvider.System).TryClaimAsync("stripe", "evt_race"),
            new EfInbox(db2, TimeProvider.System).TryClaimAsync("stripe", "evt_race"));

        Assert.Single(results, r => r);       // exactly one true
        Assert.Single(results, r => !r);      // exactly one false

        await using var read = Fixture.CreateContext();
        Assert.Equal(1, await read.Set<InboxMessage>().CountAsync());
    }

    [Fact]
    public async Task TryClaim_InRolledBackTransaction_FreesTheKeyForRedelivery()
    {
        await using (var db = Fixture.CreateContext())
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            Assert.True(await new EfInbox(db, TimeProvider.System).TryClaimAsync("stripe", "evt_rb"));
            // tx disposed without commit -> the claim rolls back, as the guarded work would have
        }

        // A redelivery after the rollback must be claimable again — proving claim+work are atomic.
        await using var db2 = Fixture.CreateContext();
        Assert.True(await new EfInbox(db2, TimeProvider.System).TryClaimAsync("stripe", "evt_rb"));

        await using var read = Fixture.CreateContext();
        Assert.Equal(1, await read.Set<InboxMessage>().CountAsync());
    }
}
