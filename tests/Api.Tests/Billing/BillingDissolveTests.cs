using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Perezosoft.Api.Services;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Core.Billing;
using Perezosoft.Core.Entities;
using Perezosoft.Infrastructure.Billing;
using Perezosoft.Infrastructure.Outbox;
using Perezosoft.Infrastructure.Repositories;

namespace Perezosoft.Api.Tests.Billing;

/// <summary>
/// Drives BILLING-7 (ADR-006 point 6): billing participates in tenant dissolve. The
/// <see cref="BillingDataContributor"/> wipes the Subscription projection and enqueues a provider cancel
/// (so a dissolved tenant stops being billed); the <see cref="BillingCancelOutboxHandler"/> performs it.
/// </summary>
[Collection(PostgresCollection.Name)]
public class BillingDissolveTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task Wipe_DeletesProjection_AndEnqueuesCancel()
    {
        var tenant = Guid.CreateVersion7();
        await SeedSubscriptionAsync(tenant, stripeSubscriptionId: "sub_123");

        await using (var db = Fixture.CreateContext(tenant))
            await Build(db).WipeAsync(tenant, default);

        await using var read = Fixture.CreateContext();
        Assert.Empty(await read.Set<Subscription>().IgnoreQueryFilters().Where(s => s.TenantId == tenant).ToListAsync());

        var message = Assert.Single(await read.Set<OutboxMessage>().Where(m => m.Type == BillingCancelOutboxHandler.MessageType).ToListAsync());
        Assert.Equal("sub_123", JsonSerializer.Deserialize<BillingCancelPayload>(message.Payload)!.StripeSubscriptionId);
    }

    [Fact]
    public async Task Wipe_NoStripeSubscription_DeletesButDoesNotEnqueueCancel()
    {
        var tenant = Guid.CreateVersion7();
        await SeedSubscriptionAsync(tenant, stripeSubscriptionId: null); // e.g. a Free/unpaid projection

        await using (var db = Fixture.CreateContext(tenant))
            await Build(db).WipeAsync(tenant, default);

        await using var read = Fixture.CreateContext();
        Assert.Empty(await read.Set<Subscription>().IgnoreQueryFilters().Where(s => s.TenantId == tenant).ToListAsync());
        Assert.Empty(await read.Set<OutboxMessage>().Where(m => m.Type == BillingCancelOutboxHandler.MessageType).ToListAsync());
    }

    [Fact]
    public async Task Wipe_NoSubscription_IsNoOp()
    {
        var tenant = Guid.CreateVersion7();
        await using (var db = Fixture.CreateContext(tenant))
            await Build(db).WipeAsync(tenant, default); // nothing to do

        await using var read = Fixture.CreateContext();
        Assert.Empty(await read.Set<OutboxMessage>().ToListAsync());
    }

    [Fact]
    public async Task HasData_IsFalse_SoBillingNeverBlocksLeaving()
    {
        var tenant = Guid.CreateVersion7();
        await SeedSubscriptionAsync(tenant, stripeSubscriptionId: "sub_123");

        await using var db = Fixture.CreateContext(tenant);
        Assert.False(await Build(db).HasDataAsync(tenant, default));
    }

    [Fact]
    public async Task Export_ReturnsPlanAndStatus_NoSecrets()
    {
        var tenant = Guid.CreateVersion7();
        await SeedSubscriptionAsync(tenant, stripeSubscriptionId: "sub_123");

        await using var db = Fixture.CreateContext(tenant);
        var export = await Build(db).ExportAsync(tenant, default);

        Assert.NotNull(export);
        var json = JsonSerializer.Serialize(export);
        Assert.Contains(PlanKeys.Pro, json);
        Assert.DoesNotContain("sub_123", json); // no Stripe ids / secrets in the export
    }

    [Fact]
    public async Task CancelHandler_CancelsAtTheProvider()
    {
        var provider = new FakeBillingProvider();
        var message = new OutboxMessage
        {
            Type = BillingCancelOutboxHandler.MessageType,
            Payload = JsonSerializer.Serialize(new BillingCancelPayload("sub_777")),
            CreatedAt = DateTimeOffset.UtcNow,
            NextAttemptAt = DateTimeOffset.UtcNow,
        };

        await new BillingCancelOutboxHandler(provider).HandleAsync(message, default);

        Assert.Equal("sub_777", Assert.Single(provider.CanceledSubscriptions));
    }

    // --- helpers ---

    private static BillingDataContributor Build(Perezosoft.Infrastructure.Persistence.AppDbContext db) =>
        new(new EfRepository<Subscription>(db), new EfOutbox(db, TimeProvider.System));

    private async Task SeedSubscriptionAsync(Guid tenant, string? stripeSubscriptionId)
    {
        await using var db = Fixture.CreateContext(tenant); // interceptor stamps TenantId
        db.Set<Subscription>().Add(new Subscription
        {
            PlanKey = PlanKeys.Pro,
            Status = SubscriptionStatus.Active,
            StripeCustomerId = "cus_1",
            StripeSubscriptionId = stripeSubscriptionId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
