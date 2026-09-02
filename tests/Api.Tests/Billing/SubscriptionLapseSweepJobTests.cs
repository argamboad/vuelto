using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Billing;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Billing;

/// <summary>
/// Drives BILLING-6 (ADR-006/007): the scheduled lapse sweep. A subscription still marked active/trialing
/// whose paid period ended (no webhook) gets the owner a **one-time** "expired" nudge; the sweep records
/// that it notified (<c>LapseNotifiedAt</c>) so a second run is a no-op, and it never touches an
/// in-period subscription. Cross-tenant scan via <c>QueryAllTenants</c>, notify inside <c>EnterTenant</c>.
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

        // Owner nudged exactly once, and the stamp recorded.
        await using var read = Fixture.CreateContext(tenant);
        var note = Assert.Single(await read.Set<Notification>().Where(n => n.UserId == ownerId).ToListAsync());
        Assert.Equal(BillingNotifications.LapsedKind, note.Kind);
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

    // --- helpers ---

    private async Task RunSweepAsync(TimeProvider clock)
    {
        var ctx = new HttpCurrentTenant(new HttpContextAccessor()); // no ambient tenant; job enters each
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Fixture.ConnectionString).Options, ctx);

        var notifier = new BillingNotifier(
            new TenantRepository(db),
            new NotificationService(
                new EfRepository<Notification>(db), new EfRepository<NotificationPreference>(db),
                new UserRepository(db), new NoopEmailSender(), clock));

        var job = new SubscriptionLapseSweepJob(
            new EfRepository<Subscription>(db), ctx, notifier, clock, NullLogger<SubscriptionLapseSweepJob>.Instance);

        await job.RunAsync();
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
