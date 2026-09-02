using Microsoft.Extensions.Time.Testing;
using Perezosoft.Api.Services;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Core.Billing;
using Perezosoft.Core.Entities;
using Perezosoft.Infrastructure.Repositories;

namespace Perezosoft.Api.Tests.Billing;

/// <summary>
/// Drives BILLING-1 (ADR-006): entitlements resolve from the tenant's Subscription projection and
/// **fail closed to Free** — no subscription, a non-active status, or a lapsed period never grant a
/// paid entitlement. Real Postgres so the tenant-scoped read goes through the global query filter.
/// </summary>
[Collection(PostgresCollection.Name)]
public class EntitlementServiceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task NoSubscription_TreatedAsFree_DeniesProFeature()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);
        var service = new EntitlementService(new EfRepository<Subscription>(db), TimeProvider.System);

        Assert.False(await service.HasAsync(Entitlements.ProFeature));
    }

    [Fact]
    public async Task ActiveProSubscription_GrantsProFeature()
    {
        var tenant = Guid.CreateVersion7();
        await SeedAsync(tenant, PlanKeys.Pro, SubscriptionStatus.Active, periodEnd: null);

        await using var db = Fixture.CreateContext(tenant);
        var service = new EntitlementService(new EfRepository<Subscription>(db), TimeProvider.System);

        Assert.True(await service.HasAsync(Entitlements.ProFeature));
    }

    [Fact]
    public async Task TrialingSubscription_GrantsProFeature()
    {
        var tenant = Guid.CreateVersion7();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 25, 0, 0, 0, TimeSpan.Zero));
        await SeedAsync(tenant, PlanKeys.Pro, SubscriptionStatus.Trialing, periodEnd: clock.GetUtcNow().AddDays(7));

        await using var db = Fixture.CreateContext(tenant);
        var service = new EntitlementService(new EfRepository<Subscription>(db), clock);

        Assert.True(await service.HasAsync(Entitlements.ProFeature));
    }

    [Theory]
    [InlineData(SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.Canceled)]
    [InlineData("incomplete")]        // Stripe statuses the projection doesn't model —
    [InlineData("unpaid")]            // — must fail closed, not default-grant
    [InlineData("some_future_status")] // an unrecognized status must NEVER grant (v3 TB-BILL, T45b)
    public async Task InactiveSubscription_FailsClosedToFree(string status)
    {
        var tenant = Guid.CreateVersion7();
        await SeedAsync(tenant, PlanKeys.Pro, status, periodEnd: null);

        await using var db = Fixture.CreateContext(tenant);
        var service = new EntitlementService(new EfRepository<Subscription>(db), TimeProvider.System);

        Assert.False(await service.HasAsync(Entitlements.ProFeature));
    }

    [Fact]
    public void StatusMap_GrantsExactlyActiveAndTrialing_ForEveryDeclaredStatus()
    {
        // v3 TB-BILL backfill (T45b): the status map, exhaustively. Every status CONSTANT the entity
        // declares is classified here by reflection — adding a new constant fails this test until its
        // granting verdict is recorded, so a "grandfathered"/"paused" addition can't silently grant.
        var granting = new HashSet<string> { SubscriptionStatus.Active, SubscriptionStatus.Trialing };

        var declared = typeof(SubscriptionStatus)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        Assert.NotEmpty(declared); // probe alive

        foreach (var status in declared)
            Assert.Equal(granting.Contains(status), SubscriptionStatus.IsGranting(status));
        Assert.False(SubscriptionStatus.IsGranting(null));      // no subscription
        Assert.False(SubscriptionStatus.IsGranting("unknown")); // fail closed on anything else
    }

    [Fact]
    public async Task ActiveButPeriodEnded_FailsClosedToFree()
    {
        var tenant = Guid.CreateVersion7();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 25, 0, 0, 0, TimeSpan.Zero));
        await SeedAsync(tenant, PlanKeys.Pro, SubscriptionStatus.Active, periodEnd: clock.GetUtcNow().AddDays(-1));

        await using var db = Fixture.CreateContext(tenant);
        var service = new EntitlementService(new EfRepository<Subscription>(db), clock);

        Assert.False(await service.HasAsync(Entitlements.ProFeature));
    }

    private async Task SeedAsync(Guid tenant, string plan, string status, DateTimeOffset? periodEnd)
    {
        await using var db = Fixture.CreateContext(tenant); // interceptor stamps TenantId
        db.Set<Subscription>().Add(new Subscription
        {
            PlanKey = plan,
            Status = status,
            CurrentPeriodEnd = periodEnd,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
