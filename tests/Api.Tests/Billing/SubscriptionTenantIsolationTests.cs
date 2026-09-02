using Microsoft.EntityFrameworkCore;
using Perezosoft.Api.Services;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Core.Billing;
using Perezosoft.Core.Entities;
using Perezosoft.Infrastructure.Repositories;

namespace Perezosoft.Api.Tests.Billing;

/// <summary>
/// v2 audit B8-1: the write-side tenancy negative for the billing projection. A tenant's
/// <see cref="Subscription"/> is <c>ITenantScoped</c>, so tenant A must not be able to READ tenant B's
/// row (entitlements fail closed to Free through the global filter) nor WRITE it (the stamping
/// interceptor rejects a foreign UPDATE/DELETE loaded via the escape hatch — ADV-1/B2, exercised here
/// on the concrete billing entity, not just the test fixture). Real Postgres so both guards run.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SubscriptionTenantIsolationTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task Subscription_TenantA_CannotReadTenantBSubscription()
    {
        var tenantB = Guid.CreateVersion7();
        await SeedActiveProAsync(tenantB);

        var tenantA = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenantA);

        // Direct read is filtered away…
        Assert.Empty(await db.Set<Subscription>().ToListAsync());
        // …and the entitlement resolver therefore fails closed to Free for A.
        var entitlements = new EntitlementService(new EfRepository<Subscription>(db), TimeProvider.System);
        Assert.False(await entitlements.HasAsync(Entitlements.ProFeature));
    }

    [Fact]
    public async Task Subscription_TenantA_CannotUpdateTenantBSubscription()
    {
        var tenantB = Guid.CreateVersion7();
        await SeedActiveProAsync(tenantB);
        var tenantA = Guid.CreateVersion7();

        await using (var db = Fixture.CreateContext(tenantA))
        {
            var foreign = await db.Set<Subscription>().IgnoreQueryFilters().SingleAsync();
            foreign.Status = SubscriptionStatus.Canceled; // attempt to tamper with B's billing state
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using var read = Fixture.CreateContext(tenantB);
        Assert.Equal(SubscriptionStatus.Active, (await read.Set<Subscription>().SingleAsync()).Status); // unchanged
    }

    [Fact]
    public async Task Subscription_TenantA_CannotDeleteTenantBSubscription()
    {
        var tenantB = Guid.CreateVersion7();
        await SeedActiveProAsync(tenantB);
        var tenantA = Guid.CreateVersion7();

        await using (var db = Fixture.CreateContext(tenantA))
        {
            var foreign = await db.Set<Subscription>().IgnoreQueryFilters().SingleAsync();
            db.Set<Subscription>().Remove(foreign);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using var read = Fixture.CreateContext(tenantB);
        Assert.Single(await read.Set<Subscription>().ToListAsync()); // still there
    }

    private async Task SeedActiveProAsync(Guid tenant)
    {
        await using var db = Fixture.CreateContext(tenant); // interceptor stamps TenantId
        db.Set<Subscription>().Add(new Subscription
        {
            PlanKey = PlanKeys.Pro,
            Status = SubscriptionStatus.Active,
            CurrentPeriodEnd = null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
