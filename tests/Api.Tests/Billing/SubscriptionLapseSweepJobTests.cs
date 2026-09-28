using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Configuration;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Billing;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;
using Vuelto.Infrastructure.Email;
using Vuelto.Infrastructure.Outbox;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Billing;

/// <summary>
/// Drives BILLING-6 (ADR-006/007): the scheduled lapse sweep. A subscription still marked active/trialing
/// whose paid period ended (no webhook) gets the owner a **one-time** "expired" nudge; the sweep records
/// that it notified (<c>LapseNotifiedAt</c>) so a second run is a no-op, and it never touches an
/// in-period subscription. Cross-tenant scan via <c>QueryAllTenants</c>, notify inside <c>EnterTenant</c>.
/// The sweep runs with the REAL outbox email sender (v4 T43, R133): the owner's email copy is a SaveChanges
/// of its own mid-way through the nudge, which is exactly what a no-op sender hid — the notification committed
/// before the stamp, and a stamp that failed after it re-nudged the owner every six hours.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SubscriptionLapseSweepJobTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task LapsedSubscription_NotifiesOwnerOnce_AndStamps()
    {
        var tenant = Guid.CreateVersion7();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var ownerId = await SeedAsync(tenant, SubscriptionStatus.Active, periodEnd: clock.GetUtcNow().AddDays(-1));

        await RunSweepAsync(clock);

        // Owner nudged exactly once — the in-app row AND the email on the outbox — and the stamp recorded.
        await using var read = Fixture.CreateContext(tenant);
        var note = Assert.Single(await read.Set<Notification>().Where(n => n.UserId == ownerId).ToListAsync());
        Assert.Equal(BillingNotifications.LapsedKind, note.Kind);
        Assert.Single(await read.Set<OutboxMessage>().Where(m => m.Type == OutboxEmailSender.MessageType && m.TenantId == tenant).ToListAsync());
        var sub = await read.Set<Subscription>().SingleAsync();
        Assert.NotNull(sub.LapseNotifiedAt);

        // A second sweep does not re-notify (idempotent per lapse).
        await RunSweepAsync(clock);
        await using var read2 = Fixture.CreateContext(tenant);
        Assert.Single(await read2.Set<Notification>().Where(n => n.UserId == ownerId).ToListAsync());
    }

    [Fact]
    public async Task InPeriodSubscription_IsNotNudged()
    {
        var tenant = Guid.CreateVersion7();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var ownerId = await SeedAsync(tenant, SubscriptionStatus.Active, periodEnd: clock.GetUtcNow().AddDays(10));

        await RunSweepAsync(clock);

        await using var read = Fixture.CreateContext(tenant);
        Assert.Empty(await read.Set<Notification>().Where(n => n.UserId == ownerId).ToListAsync());
    }

    [Fact]
    public async Task RenewedSubscription_LapsingAgain_NotifiesAgain()
    {
        // v3 TB-BILL backfill (T45b): "once per lapse", not "once per lifetime". After a renewal
        // (webhook advances CurrentPeriodEnd past the old stamp) a SECOND lapse must nudge again —
        // the `LapseNotifiedAt < CurrentPeriodEnd` predicate is what re-arms the sweep.
        var tenant = Guid.CreateVersion7();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var ownerId = await SeedAsync(tenant, SubscriptionStatus.Active, periodEnd: clock.GetUtcNow().AddDays(-1));

        await RunSweepAsync(clock); // first lapse → first nudge

        // The tenant resubscribes: a webhook moves the period end into the future…
        await using (var write = Fixture.CreateContext(tenant))
        {
            var sub = await write.Set<Subscription>().SingleAsync();
            sub.CurrentPeriodEnd = clock.GetUtcNow().AddDays(30);
            await write.SaveChangesAsync();
        }

        // …and that period lapses too.
        clock.Advance(TimeSpan.FromDays(31));
        await RunSweepAsync(clock);

        await using var read = Fixture.CreateContext(tenant);
        Assert.Equal(2, await read.Set<Notification>().CountAsync(n => n.UserId == ownerId));
    }

    [Fact]
    public async Task PastDueSubscription_IsNotNudged_DunningAlreadyDid()
    {
        // The sweep targets subscriptions still MARKED live (active/trialing) whose period silently
        // ended. A past_due sub already got the dunning notification from the webhook path — the
        // sweep re-nudging it would double-notify the owner for the same failure.
        var tenant = Guid.CreateVersion7();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var ownerId = await SeedAsync(tenant, SubscriptionStatus.PastDue, periodEnd: clock.GetUtcNow().AddDays(-5));

        await RunSweepAsync(clock);

        await using var read = Fixture.CreateContext(tenant);
        Assert.Empty(await read.Set<Notification>().Where(n => n.UserId == ownerId).ToListAsync());
    }

    [Fact]
    public async Task OwnerlessLapsedTenant_IsStampedWithoutNotifying_AndTheSweepContinues()
    {
        // A mid-dissolve tenant can be ownerless when the sweep fires. The notifier no-ops, the sub
        // is still stamped (so the sweep doesn't retry it forever), and — the IScheduledJob contract —
        // the sweep continues to the NEXT lapsed tenant rather than aborting the pass.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));

        // Ownerless: subscription + tenant but NO membership rows. Seeded FIRST so the sweep visits
        // it before the healthy tenant (order pins "continues past it").
        var ownerless = Guid.CreateVersion7();
        await using (var seed = Fixture.CreateContext(ownerless))
        {
            seed.Set<Tenant>().Add(new Tenant { Id = ownerless, Name = "Ghost", CreatedAt = DateTimeOffset.UtcNow });
            seed.Set<Subscription>().Add(new Subscription
            {
                PlanKey = PlanKeys.Pro, Status = SubscriptionStatus.Active,
                CurrentPeriodEnd = clock.GetUtcNow().AddDays(-2),
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }
        var healthy = Guid.CreateVersion7();
        var ownerId = await SeedAsync(healthy, SubscriptionStatus.Active, periodEnd: clock.GetUtcNow().AddDays(-1));

        await RunSweepAsync(clock);

        await using var read = Fixture.CreateContext();
        var ghost = await read.Set<Subscription>().IgnoreQueryFilters().SingleAsync(s => s.TenantId == ownerless);
        Assert.NotNull(ghost.LapseNotifiedAt); // stamped → not retried every 6h forever
        await using var readHealthy = Fixture.CreateContext(healthy);
        Assert.Single(await readHealthy.Set<Notification>().Where(n => n.UserId == ownerId).ToListAsync());
    }

    [Fact]
    public async Task StampFailure_AfterTheNotification_LeavesNeither()
    {
        // v4 T43 (LB-BILL-24, R133): the owner's email copy is enqueued by OutboxEmailSender with a SaveChanges of
        // its own, BEFORE the stamp. Without one transaction around the pair, a stamp that failed left the
        // notification (and the email) committed, and the next sweep — six hours later — sent them again, for as
        // long as the stamp kept failing. The fault is injected at the repository (the DB-fault seam is T54's):
        // the stamp's SaveChanges throws, and nothing of the nudge survives.
        var tenant = Guid.CreateVersion7();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var ownerId = await SeedAsync(tenant, SubscriptionStatus.Active, periodEnd: clock.GetUtcNow().AddDays(-1));

        await RunSweepAsync(clock, subscriptions: inner => new FailingOnSave(inner));

        await using var read = Fixture.CreateContext(tenant);
        Assert.Empty(await read.Set<Notification>().Where(n => n.UserId == ownerId).ToListAsync());
        Assert.Empty(await read.Set<OutboxMessage>().Where(m => m.TenantId == tenant).ToListAsync());
        Assert.Null((await read.Set<Subscription>().SingleAsync()).LapseNotifiedAt);

        // ...and the next sweep, with the fault gone, delivers exactly one nudge.
        await RunSweepAsync(clock);
        await using var read2 = Fixture.CreateContext(tenant);
        Assert.Single(await read2.Set<Notification>().Where(n => n.UserId == ownerId).ToListAsync());
        Assert.Single(await read2.Set<OutboxMessage>().Where(m => m.TenantId == tenant).ToListAsync());
    }

    [Fact]
    public async Task BillingOff_LapsedSubscription_IsNotNudged()
    {
        // v4 T43 (BILL-5): with the billing gate off (GATES-1) there is no billing page to "resubscribe from" —
        // the route is not even built — yet a subscription row left over from before the gate was closed still
        // lapses. The sweep does nothing in that mode: no nudge, no email, and no stamp either (nothing happened).
        var tenant = Guid.CreateVersion7();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var ownerId = await SeedAsync(tenant, SubscriptionStatus.Active, periodEnd: clock.GetUtcNow().AddDays(-1));

        await RunSweepAsync(clock, billingEnabled: false);

        await using var read = Fixture.CreateContext(tenant);
        Assert.Empty(await read.Set<Notification>().Where(n => n.UserId == ownerId).ToListAsync());
        Assert.Empty(await read.Set<OutboxMessage>().Where(m => m.TenantId == tenant).ToListAsync());
        Assert.Null((await read.Set<Subscription>().SingleAsync()).LapseNotifiedAt);
    }

    // --- helpers ---

    private async Task RunSweepAsync(TimeProvider clock, bool billingEnabled = true,
        Func<IRepository<Subscription>, IRepository<Subscription>>? subscriptions = null)
    {
        var ctx = new HttpCurrentTenant(new HttpContextAccessor()); // no ambient tenant; job enters each
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Fixture.ConnectionString).Options, ctx);

        // The real app-facing sender (ADR-007): the email copy rides the outbox and flushes the context itself.
        var emailSender = new OutboxEmailSender(new EfOutbox(db, clock), db, ctx);
        var notifier = new BillingNotifier(
            new TenantRepository(db),
            new NotificationService(
                new EfRepository<Notification>(db), new EfRepository<NotificationPreference>(db),
                new UserRepository(db), emailSender, clock));

        IRepository<Subscription> subs = new EfRepository<Subscription>(db);
        var job = new SubscriptionLapseSweepJob(
            subscriptions?.Invoke(subs) ?? subs, ctx, notifier, new EfUnitOfWork(db),
            new BillingSettings { Enabled = billingEnabled }, clock, NullLogger<SubscriptionLapseSweepJob>.Instance);

        await job.RunAsync();
    }

    /// <summary>The stamp's SaveChanges fails; everything else is the real repository.</summary>
    private sealed class FailingOnSave(IRepository<Subscription> inner) : IRepository<Subscription>
    {
        public IQueryable<Subscription> Query() => inner.Query();
        public IQueryable<Subscription> QueryAllTenants() => inner.QueryAllTenants();
        public Task AddAsync(Subscription entity, CancellationToken cancellationToken = default) => inner.AddAsync(entity, cancellationToken);
        public void Update(Subscription entity) => inner.Update(entity);
        public void Remove(Subscription entity) => inner.Remove(entity);
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("injected: the stamp did not land");
    }

    private async Task<Guid> SeedAsync(Guid tenant, string status, DateTimeOffset periodEnd)
    {
        var ownerId = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant); // interceptor stamps TenantId on the subscription
        db.Set<Tenant>().Add(new Tenant { Id = tenant, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
        db.Set<User>().Add(new User { Id = ownerId, Email = $"owner-{ownerId:N}@x.com" });
        db.Set<TenantMembership>().Add(new TenantMembership { TenantId = tenant, UserId = ownerId, Role = TenantRoles.Owner });
        db.Set<Subscription>().Add(new Subscription
        {
            PlanKey = PlanKeys.Pro,
            Status = status,
            CurrentPeriodEnd = periodEnd,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return ownerId;
    }
}
