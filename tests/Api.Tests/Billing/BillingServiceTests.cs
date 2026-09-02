using Perezosoft.Api.Services;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Core.Billing;
using Perezosoft.Core.Entities;
using Perezosoft.Infrastructure.Billing;
using Perezosoft.Infrastructure.Repositories;

namespace Perezosoft.Api.Tests.Billing;

/// <summary>
/// BILLING-2/4 orchestration (ADR-006): checkout plan validation + provider invocation, and the portal
/// session (reads the current tenant's <c>Subscription.StripeCustomerId</c>, or reports no
/// subscription). Real Postgres because the service reads the tenant-scoped subscription; the owner gate
/// is a controller concern (<see cref="BillingControllerTests"/>).
/// </summary>
[Collection(PostgresCollection.Name)]
public class BillingServiceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task ValidPaidPlan_CreatesSession_CarryingTenantAndPlan()
    {
        await using var db = Fixture.CreateContext();
        var fake = new FakeBillingProvider();
        var service = new BillingService(fake, new TestAppSettings(), new EfRepository<Subscription>(db));
        var tenantId = Guid.CreateVersion7();

        var result = await service.CreateCheckoutAsync(tenantId, PlanKeys.Pro);

        Assert.Equal(CheckoutOutcome.Created, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.Url));
        var request = Assert.Single(fake.Requests);
        Assert.Equal(tenantId, request.TenantId);
        Assert.Equal(PlanKeys.Pro, request.PlanKey);
    }

    [Theory]
    [InlineData(PlanKeys.Free)] // Free isn't purchasable
    [InlineData("gold")]        // unknown plan
    [InlineData(null)]
    public async Task UnknownOrFreePlan_IsRejected_NoSession(string? planKey)
    {
        await using var db = Fixture.CreateContext();
        var fake = new FakeBillingProvider();
        var service = new BillingService(fake, new TestAppSettings(), new EfRepository<Subscription>(db));

        var result = await service.CreateCheckoutAsync(Guid.CreateVersion7(), planKey);

        Assert.Equal(CheckoutOutcome.InvalidPlan, result.Outcome);
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Portal_WithSubscription_OpensPortalForThatCustomer()
    {
        var tenant = Guid.CreateVersion7();
        await SeedSubscriptionAsync(tenant, "cus_123");

        await using var db = Fixture.CreateContext(tenant); // scoped to the tenant
        var fake = new FakeBillingProvider();
        var service = new BillingService(fake, new TestAppSettings(), new EfRepository<Subscription>(db));

        var result = await service.CreatePortalAsync();

        Assert.Equal(PortalOutcome.Created, result.Outcome);
        Assert.False(string.IsNullOrEmpty(result.Url));
        Assert.Equal("cus_123", Assert.Single(fake.PortalRequests).StripeCustomerId);
    }

    [Fact]
    public async Task Portal_WithoutSubscription_ReportsNoSubscription()
    {
        await using var db = Fixture.CreateContext(Guid.CreateVersion7());
        var fake = new FakeBillingProvider();
        var service = new BillingService(fake, new TestAppSettings(), new EfRepository<Subscription>(db));

        var result = await service.CreatePortalAsync();

        Assert.Equal(PortalOutcome.NoSubscription, result.Outcome);
        Assert.Empty(fake.PortalRequests);
    }

    private async Task SeedSubscriptionAsync(Guid tenant, string stripeCustomerId)
    {
        await using var db = Fixture.CreateContext(tenant);
        db.Set<Subscription>().Add(new Subscription
        {
            PlanKey = PlanKeys.Pro,
            Status = SubscriptionStatus.Active,
            StripeCustomerId = stripeCustomerId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
