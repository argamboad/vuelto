using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Vuelto.Api.Controllers;
using Vuelto.Api.Models;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Billing;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Billing;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Billing;

/// <summary>
/// BILLING-2 owner gate on the platform <see cref="BillingController"/>: only the tenant <b>owner</b>
/// can start checkout. Exercises the controller through the real <c>TenantApiControllerBase</c> gate
/// with a Postgres-backed membership lookup and a claims principal for the caller.
/// </summary>
[Collection(PostgresCollection.Name)]
public class BillingControllerTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task Owner_Checkout_ReturnsOkWithUrl()
    {
        var ownerId = await SeedMembershipAsync(Guid.CreateVersion7(), TenantRoles.Owner);
        await using var db = Fixture.CreateContext();
        var fake = new FakeBillingProvider();
        var controller = NewController(db, fake, ownerId);

        var result = await controller.Checkout(new CreateCheckoutRequest { PlanKey = PlanKeys.Pro }, default);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<CheckoutResponse>(ok.Value);
        Assert.Single(fake.Requests);
    }

    [Fact]
    public async Task Member_Checkout_Returns403_NoSession()
    {
        var tenantId = Guid.CreateVersion7();
        await SeedMembershipAsync(tenantId, TenantRoles.Owner);            // tenant must have an owner
        var memberId = await SeedMembershipAsync(tenantId, TenantRoles.Member);
        await using var db = Fixture.CreateContext();

        // The owner gate now lives in [RequireTenantPermission] (B9-5); run it as the pipeline would.
        var result = await TenantPermissionGate.RunAsync(
            typeof(BillingController), nameof(BillingController.Checkout), new TenantRepository(db), memberId, tenantId);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
    }

    [Fact]
    public async Task NoMembership_Returns401()
    {
        await using var db = Fixture.CreateContext();
        var controller = NewController(db, new FakeBillingProvider(), Guid.CreateVersion7());

        var result = await controller.Checkout(new CreateCheckoutRequest { PlanKey = PlanKeys.Pro }, default);

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    // --- BILLING-4: portal (owner only; needs a subscription) ---

    [Fact]
    public async Task Owner_Portal_WithSubscription_ReturnsOkWithUrl()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = await SeedMembershipAsync(tenantId, TenantRoles.Owner);
        await SeedSubscriptionAsync(tenantId, "cus_123");

        await using var db = Fixture.CreateContext(tenantId); // scoped so the service reads this tenant's sub
        var fake = new FakeBillingProvider();
        var controller = NewController(db, fake, ownerId);

        var result = await controller.Portal(default);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<PortalResponse>(ok.Value);
        Assert.Equal("cus_123", Assert.Single(fake.PortalRequests).StripeCustomerId);
    }

    [Fact]
    public async Task Owner_Portal_WithoutSubscription_Returns400()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = await SeedMembershipAsync(tenantId, TenantRoles.Owner);

        await using var db = Fixture.CreateContext(tenantId);
        var controller = NewController(db, new FakeBillingProvider(), ownerId);

        var result = await controller.Portal(default);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Member_Portal_Returns403()
    {
        var tenantId = Guid.CreateVersion7();
        await SeedMembershipAsync(tenantId, TenantRoles.Owner);
        var memberId = await SeedMembershipAsync(tenantId, TenantRoles.Member);

        await using var db = Fixture.CreateContext(tenantId);

        // The owner gate now lives in [RequireTenantPermission] (B9-5); run it as the pipeline would.
        var result = await TenantPermissionGate.RunAsync(
            typeof(BillingController), nameof(BillingController.Portal), new TenantRepository(db), memberId, tenantId);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
    }

    // --- BILLING-8: owner summary (plan + seats for the billing page) ---

    [Fact]
    public async Task Owner_Summary_FreshTenant_IsFreePlanWithSeatUsage()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = await SeedMembershipAsync(tenantId, TenantRoles.Owner);

        await using var db = Fixture.CreateContext(tenantId);
        var controller = NewController(db, new FakeBillingProvider(), ownerId, tenantId);

        var ok = Assert.IsType<OkObjectResult>(await controller.Summary(default));
        var summary = Assert.IsType<BillingSummaryResponse>(ok.Value);
        Assert.Equal(PlanKeys.Free, summary.PlanKey);
        Assert.Equal("none", summary.Status);
        Assert.False(summary.HasSubscription);
        Assert.Equal(1, summary.Seats.Used);           // just the owner
        Assert.Equal(3, summary.Seats.Limit);          // free-plan example quota
    }

    [Fact]
    public async Task Owner_Summary_ActiveProSubscription_IsPro()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = await SeedMembershipAsync(tenantId, TenantRoles.Owner);
        await SeedSubscriptionAsync(tenantId, "cus_summary");

        await using var db = Fixture.CreateContext(tenantId);
        var controller = NewController(db, new FakeBillingProvider(), ownerId, tenantId);

        var ok = Assert.IsType<OkObjectResult>(await controller.Summary(default));
        var summary = Assert.IsType<BillingSummaryResponse>(ok.Value);
        Assert.Equal(PlanKeys.Pro, summary.PlanKey);
        Assert.Equal(SubscriptionStatus.Active, summary.Status);
        Assert.True(summary.HasSubscription);
        Assert.Equal(10, summary.Seats.Limit);         // pro-plan example quota
    }

    [Fact]
    public async Task Owner_Summary_LapsedSubscription_FailsClosedToFree()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = await SeedMembershipAsync(tenantId, TenantRoles.Owner);
        await SeedSubscriptionAsync(tenantId, "cus_lapsed", currentPeriodEnd: DateTimeOffset.UtcNow.AddDays(-1));

        await using var db = Fixture.CreateContext(tenantId);
        var controller = NewController(db, new FakeBillingProvider(), ownerId, tenantId);

        var ok = Assert.IsType<OkObjectResult>(await controller.Summary(default));
        var summary = Assert.IsType<BillingSummaryResponse>(ok.Value);
        Assert.Equal(PlanKeys.Free, summary.PlanKey);  // lapsed → fail-closed
        Assert.Equal(3, summary.Seats.Limit);
        Assert.True(summary.HasSubscription);          // portal still reachable to fix payment
    }

    [Fact]
    public async Task Member_Summary_Returns403()
    {
        var tenantId = Guid.CreateVersion7();
        await SeedMembershipAsync(tenantId, TenantRoles.Owner);
        var memberId = await SeedMembershipAsync(tenantId, TenantRoles.Member);
        await using var db = Fixture.CreateContext(tenantId);

        var result = await TenantPermissionGate.RunAsync(
            typeof(BillingController), nameof(BillingController.Summary), new TenantRepository(db), memberId, tenantId);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
    }

    private static BillingController NewController(AppDbContext db, FakeBillingProvider billing, Guid currentUserId)
        => NewController(db, billing, currentUserId, Guid.CreateVersion7());

    private static BillingController NewController(AppDbContext db, FakeBillingProvider billing, Guid currentUserId, Guid tenantId)
    {
        var controller = new BillingController(
            new TenantRepository(db),
            new BillingService(billing, new TestAppSettings(), new EfRepository<Subscription>(db)),
            new EfRepository<Subscription>(db),
            new QuotaService(new EfRepository<Subscription>(db), new TenantRepository(db),
                new TenantInvitationRepository(db), new EfRepository<UsageCounter>(db),
                new TestCurrentTenant { TenantId = tenantId }, TimeProvider.System),
            TimeProvider.System);

        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, currentUserId.ToString())], authenticationType: "test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    /// <summary>Adds a (Tenant, User, TenantMembership) and returns the new user id. Not tenant-scoped.</summary>
    private async Task<Guid> SeedMembershipAsync(Guid tenantId, string role)
    {
        var userId = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext();
        if (!await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(db.Set<Tenant>(), t => t.Id == tenantId))
            db.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = "Tenant" });
        db.Set<User>().Add(new User { Id = userId, Email = $"u-{userId:N}@x.com" });
        db.Set<TenantMembership>().Add(new TenantMembership { TenantId = tenantId, UserId = userId, Role = role });
        await db.SaveChangesAsync();
        return userId;
    }

    private async Task SeedSubscriptionAsync(Guid tenantId, string stripeCustomerId, DateTimeOffset? currentPeriodEnd = null)
    {
        await using var db = Fixture.CreateContext(tenantId); // interceptor stamps TenantId
        db.Set<Subscription>().Add(new Subscription
        {
            PlanKey = PlanKeys.Pro,
            Status = SubscriptionStatus.Active,
            StripeCustomerId = stripeCustomerId,
            CurrentPeriodEnd = currentPeriodEnd,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
