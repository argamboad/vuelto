using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Billing;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Billing;

/// <summary>
/// Drives BILLING-5 (ADR-006): plan **quotas** — seats (members + pending invites vs the plan's seat
/// limit) and metered usage (a monthly counter). Plan resolves from the tenant's Subscription, failing
/// closed to Free; a missing limit means unlimited. Real Postgres so seat counts + usage rows are real.
/// </summary>
[Collection(PostgresCollection.Name)]
public class QuotaServiceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    // Catalog example limits this suite pins to: Free seats=3 / export=3; Pro seats=10.

    [Fact]
    public async Task Seats_UnderFreeLimit_CanAdd()
    {
        var tenant = Guid.CreateVersion7();
        await SeedTenantWithMembersAsync(tenant, members: 2); // Free (no sub) → limit 3

        await using var db = Fixture.CreateContext(tenant);
        var quota = BuildQuota(db, tenant);

        var usage = await quota.GetSeatUsageAsync();
        Assert.Equal(2, usage.Used);
        Assert.Equal(3, usage.Limit);
        Assert.True(await quota.CanAddSeatsAsync());
    }

    [Fact]
    public async Task Seats_AtFreeLimit_CannotAdd()
    {
        var tenant = Guid.CreateVersion7();
        await SeedTenantWithMembersAsync(tenant, members: 3);

        await using var db = Fixture.CreateContext(tenant);
        var quota = BuildQuota(db, tenant);

        Assert.True((await quota.GetSeatUsageAsync()).AtLimit);
        Assert.False(await quota.CanAddSeatsAsync());
    }

    [Fact]
    public async Task Seats_PendingInvitesCountTowardSeats()
    {
        var tenant = Guid.CreateVersion7();
        await SeedTenantWithMembersAsync(tenant, members: 2);
        await SeedPendingInviteAsync(tenant, "pending@x.com"); // 2 members + 1 pending = 3

        await using var db = Fixture.CreateContext(tenant);
        var quota = BuildQuota(db, tenant);

        Assert.Equal(3, (await quota.GetSeatUsageAsync()).Used);
        Assert.False(await quota.CanAddSeatsAsync());
    }

    [Fact]
    public async Task Seats_ProPlan_RaisesLimit()
    {
        var tenant = Guid.CreateVersion7();
        await SeedTenantWithMembersAsync(tenant, members: 3);
        await SeedSubscriptionAsync(tenant, PlanKeys.Pro, SubscriptionStatus.Active);

        await using var db = Fixture.CreateContext(tenant);
        var quota = BuildQuota(db, tenant);

        var usage = await quota.GetSeatUsageAsync();
        Assert.Equal(10, usage.Limit);
        Assert.True(await quota.CanAddSeatsAsync());
    }

    [Fact]
    public async Task TryConsume_WithinLimit_IncrementsAndAllows()
    {
        var tenant = Guid.CreateVersion7();
        await SeedTenantWithMembersAsync(tenant, members: 1);

        await using var db = Fixture.CreateContext(tenant);
        var quota = BuildQuota(db, tenant);

        Assert.True(await quota.TryConsumeAsync(UsageKeys.Export));
        Assert.True(await quota.TryConsumeAsync(UsageKeys.Export));

        // Read on a fresh context — the atomic ExecuteUpdate writes straight to the DB.
        await using var read = Fixture.CreateContext(tenant);
        var count = (await new EfRepository<UsageCounter>(read).Query().ToListAsync())
            .Where(c => c.Key == UsageKeys.Export).Sum(c => c.Count);
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task TryConsume_AtLimit_DeniesWithoutIncrement()
    {
        var tenant = Guid.CreateVersion7();
        await SeedTenantWithMembersAsync(tenant, members: 1);

        await using var db = Fixture.CreateContext(tenant);
        var quota = BuildQuota(db, tenant);

        for (var i = 0; i < 3; i++) // exhaust Free export=3
            Assert.True(await quota.TryConsumeAsync(UsageKeys.Export));

        Assert.False(await quota.TryConsumeAsync(UsageKeys.Export)); // 4th denied

        await using var read = Fixture.CreateContext(tenant);
        var count = (await new EfRepository<UsageCounter>(read).Query().ToListAsync())
            .Where(c => c.Key == UsageKeys.Export).Sum(c => c.Count);
        Assert.Equal(3, count); // not incremented past the limit
    }

    [Fact]
    public async Task TryConsume_ConcurrentAtLimit_AllowsExactlyOne()
    {
        // v2 audit LOGIC-B7: consumption must be atomic — two racing consumers at the last unit must not
        // both succeed (no lost update past the cap).
        var tenant = Guid.CreateVersion7();
        await SeedTenantWithMembersAsync(tenant, members: 1);

        await using (var seed = Fixture.CreateContext(tenant))
        {
            var q = BuildQuota(seed, tenant); // consume 2 of Free export=3 → room for exactly one more
            Assert.True(await q.TryConsumeAsync(UsageKeys.Export));
            Assert.True(await q.TryConsumeAsync(UsageKeys.Export));
        }

        async Task<bool> ConsumeOnOwnConnectionAsync()
        {
            await using var db = Fixture.CreateContext(tenant); // separate connection → real DB race
            return await BuildQuota(db, tenant).TryConsumeAsync(UsageKeys.Export);
        }

        var results = await Task.WhenAll(ConsumeOnOwnConnectionAsync(), ConsumeOnOwnConnectionAsync());

        Assert.Equal(1, results.Count(r => r)); // exactly one consumer wins the last unit
        await using var read = Fixture.CreateContext(tenant);
        var total = (await new EfRepository<UsageCounter>(read).Query().ToListAsync())
            .Where(c => c.Key == UsageKeys.Export).Sum(c => c.Count);
        Assert.Equal(3, total); // never past the cap
    }

    [Fact]
    public async Task TryConsume_UnlimitedKey_AllowsWithoutTracking()
    {
        var tenant = Guid.CreateVersion7();
        await SeedTenantWithMembersAsync(tenant, members: 1);

        await using var db = Fixture.CreateContext(tenant);
        var quota = BuildQuota(db, tenant);

        Assert.True(await quota.TryConsumeAsync("no-limit-key"));
        Assert.Empty(await new EfRepository<UsageCounter>(db).Query().ToListAsync());
    }

    [Fact]
    public async Task TryConsume_NewMonth_ResetsCounter()
    {
        var tenant = Guid.CreateVersion7();
        await SeedTenantWithMembersAsync(tenant, members: 1);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        await using var db = Fixture.CreateContext(tenant);
        var quota = BuildQuota(db, tenant, clock);

        for (var i = 0; i < 3; i++) Assert.True(await quota.TryConsumeAsync(UsageKeys.Export));
        Assert.False(await quota.TryConsumeAsync(UsageKeys.Export)); // July exhausted

        clock.Advance(TimeSpan.FromDays(32)); // into August
        Assert.True(await quota.TryConsumeAsync(UsageKeys.Export)); // fresh period
    }

    [Fact]
    public async Task TryConsume_YearRollover_ResetsCounter()
    {
        // v3 TB-BILL backfill (T45b): Dec→Jan is the period-key edge where a month-only key ("12" vs
        // "01") would still work but a misbuilt year component ("2026-12" vs "2027-01") could collide
        // or never reset. Pin the UTC year boundary explicitly.
        var tenant = Guid.CreateVersion7();
        await SeedTenantWithMembersAsync(tenant, members: 1);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 12, 31, 23, 30, 0, TimeSpan.Zero));

        await using var db = Fixture.CreateContext(tenant);
        var quota = BuildQuota(db, tenant, clock);

        for (var i = 0; i < 3; i++) Assert.True(await quota.TryConsumeAsync(UsageKeys.Export));
        Assert.False(await quota.TryConsumeAsync(UsageKeys.Export)); // December exhausted

        clock.Advance(TimeSpan.FromHours(1)); // 2027-01-01 00:30 UTC
        Assert.True(await quota.TryConsumeAsync(UsageKeys.Export)); // fresh year, fresh period
    }

    // --- invite-flow integration: the seat quota blocks a new invite past the cap ---

    [Fact]
    public async Task Invite_BeyondFreeSeatLimit_ReturnsSeatLimitReached()
    {
        var tenant = Guid.CreateVersion7();
        var ownerId = await SeedTenantWithMembersAsync(tenant, members: 3); // Free, at cap

        await using var db = Fixture.CreateContext(tenant);
        var svc = new ServiceHarness(db, currentTenant: new TestCurrentTenant { TenantId = tenant }).InvitationService();

        var result = await svc.CreateAsync(tenant, ownerId, "fourth@x.com");
        Assert.Equal(InviteCreateStatus.SeatLimitReached, result.Status);
    }

    [Fact]
    public async Task Invite_UnderSeatLimit_Succeeds()
    {
        var tenant = Guid.CreateVersion7();
        var ownerId = await SeedTenantWithMembersAsync(tenant, members: 2); // Free, room for one

        await using var db = Fixture.CreateContext(tenant);
        var svc = new ServiceHarness(db, currentTenant: new TestCurrentTenant { TenantId = tenant }).InvitationService();

        var result = await svc.CreateAsync(tenant, ownerId, "third@x.com");
        Assert.Equal(InviteCreateStatus.Created, result.Status);
    }

    [Fact]
    public async Task TryConsume_FirstConsume_NonUniqueDbError_Propagates()
    {
        // v3 LB-BILL-3: only a unique-(Tenant,Key,Period) violation (23505) means a concurrent insert race
        // to retry. Any OTHER DbUpdateException (serialization/deadlock/timeout) must PROPAGATE, not be
        // swallowed as a benign race — which would spuriously deny a request that had headroom or mask the
        // real fault.
        var tenant = Guid.CreateVersion7();
        await SeedTenantWithMembersAsync(tenant, members: 1);

        await using var db = Fixture.CreateContext(tenant);
        var quota = new QuotaService(
            new EfRepository<Subscription>(db), new TenantRepository(db), new TenantInvitationRepository(db),
            new ThrowingUsageRepository(new EfRepository<UsageCounter>(db)), // throws a NON-23505 on insert
            new TestCurrentTenant { TenantId = tenant }, TimeProvider.System);

        await Assert.ThrowsAsync<DbUpdateException>(() => quota.TryConsumeAsync(UsageKeys.Export));
    }

    /// <summary>A usage repo that fails the first-consume insert with a NON-unique DbUpdateException.</summary>
    private sealed class ThrowingUsageRepository(IRepository<UsageCounter> inner) : IRepository<UsageCounter>
    {
        public IQueryable<UsageCounter> Query() => inner.Query();
        public IQueryable<UsageCounter> QueryAllTenants() => inner.QueryAllTenants();
        public Task AddAsync(UsageCounter entity, CancellationToken cancellationToken = default) => inner.AddAsync(entity, cancellationToken);
        public void Update(UsageCounter entity) => inner.Update(entity);
        public void Remove(UsageCounter entity) => inner.Remove(entity);
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("simulated non-unique failure",
                new PostgresException("serialization failure", "ERROR", "ERROR", "40001")); // NOT 23505
    }

    // --- helpers ---

    private static QuotaService BuildQuota(Vuelto.Infrastructure.Persistence.AppDbContext db, Guid tenant, TimeProvider? clock = null) =>
        new(new EfRepository<Subscription>(db), new TenantRepository(db), new TenantInvitationRepository(db),
            new EfRepository<UsageCounter>(db), new TestCurrentTenant { TenantId = tenant }, clock ?? TimeProvider.System);

    /// <summary>Seeds a tenant with <paramref name="members"/> memberships; returns the owner's user id.</summary>
    private async Task<Guid> SeedTenantWithMembersAsync(Guid tenant, int members)
    {
        await using var db = Fixture.CreateContext(tenant);
        db.Set<Tenant>().Add(new Tenant { Id = tenant, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
        Guid ownerId = Guid.Empty;
        for (var i = 0; i < members; i++)
        {
            var uid = Guid.CreateVersion7();
            if (i == 0) ownerId = uid;
            db.Set<User>().Add(new User { Id = uid, Email = $"m{i}-{uid:N}@x.com" });
            db.Set<TenantMembership>().Add(new TenantMembership
            {
                TenantId = tenant,
                UserId = uid,
                Role = i == 0 ? TenantRoles.Owner : TenantRoles.Member,
            });
        }
        await db.SaveChangesAsync();
        return ownerId;
    }

    private async Task SeedPendingInviteAsync(Guid tenant, string email)
    {
        await using var db = Fixture.CreateContext(tenant);
        var inviter = Guid.CreateVersion7();
        db.Set<User>().Add(new User { Id = inviter, Email = $"inv-{inviter:N}@x.com" });
        db.Set<TenantInvitation>().Add(new TenantInvitation
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            InvitedEmail = email,
            InvitedByUserId = inviter,
            Status = InvitationStatuses.Pending,
            TokenHash = $"hash-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedSubscriptionAsync(Guid tenant, string plan, string status)
    {
        await using var db = Fixture.CreateContext(tenant);
        db.Set<Subscription>().Add(new Subscription
        {
            PlanKey = plan,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
