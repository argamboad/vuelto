using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Billing;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Billing;
using Vuelto.Infrastructure.Inbox;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Billing;

/// <summary>
/// Drives BILLING-3 (ADR-006): the webhook is what actually grants access. Verifies signature
/// rejection, inbox idempotency (ADR-007), fail-closed downgrades, and that the write lands under the
/// right tenant via <c>EnterTenant</c> (ADR-003) — through the normal tenant-scoped path, no escape
/// hatch. Offline via <see cref="FakeBillingProvider"/>; real Postgres for the inbox + projection.
/// </summary>
[Collection(PostgresCollection.Name)]
public class BillingWebhookHandlerTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task SubscriptionActivated_FlipsTenantToActivePlan()
    {
        var tenant = Guid.CreateVersion7();

        Assert.Equal(WebhookResult.Applied, await HandleAsync(Event(tenant, SubscriptionStatus.Active)));

        Assert.True(await EntitledAsync(tenant));
        await using var read = Fixture.CreateContext(tenant);
        var sub = await read.Set<Subscription>().SingleAsync();
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Equal(tenant, sub.TenantId);
    }

    [Fact]
    public async Task DuplicateEvent_IsIdempotent_NoDoubleApply()
    {
        var tenant = Guid.CreateVersion7();
        var evt = Event(tenant, SubscriptionStatus.Active);

        Assert.Equal(WebhookResult.Applied, await HandleAsync(evt));
        Assert.Equal(WebhookResult.Duplicate, await HandleAsync(evt)); // same EventId

        await using var read = Fixture.CreateContext();
        Assert.Equal(1, await read.Set<Subscription>().IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task InvalidSignature_IsRejected_NothingApplied()
    {
        var tenant = Guid.CreateVersion7();

        Assert.Equal(WebhookResult.InvalidSignature, await HandleAsync(Event(tenant, SubscriptionStatus.Active), signature: "bad"));

        await using var read = Fixture.CreateContext();
        Assert.Empty(await read.Set<Subscription>().IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task CanceledSubscription_FailsClosedToFree()
    {
        var tenant = Guid.CreateVersion7();
        await HandleAsync(Event(tenant, SubscriptionStatus.Active, eventId: "evt_1"));
        Assert.True(await EntitledAsync(tenant));

        await HandleAsync(Event(tenant, SubscriptionStatus.Canceled, eventId: "evt_2"));
        Assert.False(await EntitledAsync(tenant)); // fail closed
    }

    [Fact]
    public async Task Webhook_OverACompedSubscription_Applies_AndRestoresProviderManagement()
    {
        // v3 TB-ADM-6/7 (T45a): the comp→webhook→revert interleaving. A staff comp writes a projection
        // with NO provider ids and NO LastEventAt — so when the tenant later subscribes for real, the
        // provider webhook must APPLY over the comp (the recency guard can't block: nothing to compare)
        // and re-establish provider linkage. From that point the staff comp/revert endpoints 409 again
        // (proven by Staff_CompOrRevert_ProviderManagedSubscription_Returns409) — Stripe is the source
        // of truth for real money, and a past comp must not leave a backdoor around it.
        var tenant = Guid.CreateVersion7();
        await using (var seed = Fixture.CreateContext(tenant))
        {
            seed.Set<Subscription>().Add(new Subscription
            {
                PlanKey = PlanKeys.Pro,
                Status = SubscriptionStatus.Active,
                StripeCustomerId = null, StripeSubscriptionId = null, // a comp, not a provider sub
                CurrentPeriodEnd = null, LastEventAt = null,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        Assert.Equal(WebhookResult.Applied, await HandleAsync(Event(tenant, SubscriptionStatus.Active)));

        await using var read = Fixture.CreateContext(tenant);
        var sub = await read.Set<Subscription>().SingleAsync(); // still ONE projection, not a second row
        Assert.Equal("sub_1", sub.StripeSubscriptionId);        // provider-managed again → comp/revert 409
        Assert.NotNull(sub.LastEventAt);                        // recency guard re-armed
    }

    [Fact]
    public async Task Applied_OnlyVisibleToItsOwnTenant()
    {
        var tenant = Guid.CreateVersion7();
        await HandleAsync(Event(tenant, SubscriptionStatus.Active));

        await using var other = Fixture.CreateContext(Guid.CreateVersion7());
        Assert.Empty(await other.Set<Subscription>().ToListAsync()); // scoped by EnterTenant, not leaked
    }

    // --- dunning (BILLING-6): owner notified on failed-payment / cancel transitions ---

    [Fact]
    public async Task PaymentFailed_NotifiesOwner()
    {
        var tenant = Guid.CreateVersion7();
        var ownerId = await SeedOwnerAsync(tenant);

        await HandleAsync(Event(tenant, SubscriptionStatus.Active, eventId: "evt_a"));
        await HandleAsync(Event(tenant, SubscriptionStatus.PastDue, eventId: "evt_b"));

        await using var read = Fixture.CreateContext(tenant);
        var notes = await read.Set<Notification>().Where(n => n.UserId == ownerId).ToListAsync();
        var note = Assert.Single(notes);
        Assert.Equal(BillingNotifications.PastDueKind, note.Kind);
    }

    [Theory]
    [InlineData(SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.Canceled)]
    public async Task FirstEverEvent_NonGrantingStatus_DoesNotDun_ColdStart(string status)
    {
        // v3 LB-BILL-1/LB-BILL-4: the FIRST event for a tenant can carry past_due/canceled (a failed first
        // invoice, an abandoned checkout later canceled). previousStatus is null, so it must NOT dun — the
        // tenant never had a live subscription to lapse.
        var tenant = Guid.CreateVersion7();
        var ownerId = await SeedOwnerAsync(tenant);

        await HandleAsync(Event(tenant, status, eventId: "evt_cold"));

        await using var read = Fixture.CreateContext(tenant);
        Assert.Empty(await read.Set<Notification>().Where(n => n.UserId == ownerId).ToListAsync());
    }

    [Fact]
    public async Task ActiveToCanceled_NotifiesOwner()
    {
        // The positive control for the "transition out of a granting status" rule: a live sub that cancels
        // still duns.
        var tenant = Guid.CreateVersion7();
        var ownerId = await SeedOwnerAsync(tenant);

        await HandleAsync(Event(tenant, SubscriptionStatus.Active, eventId: "evt_a"));
        await HandleAsync(Event(tenant, SubscriptionStatus.Canceled, eventId: "evt_b"));

        await using var read = Fixture.CreateContext(tenant);
        var note = Assert.Single(await read.Set<Notification>().Where(n => n.UserId == ownerId).ToListAsync());
        Assert.Equal(BillingNotifications.CanceledKind, note.Kind);
    }

    [Fact]
    public async Task Renewal_NoStatusChange_DoesNotNotify()
    {
        var tenant = Guid.CreateVersion7();
        var ownerId = await SeedOwnerAsync(tenant);

        await HandleAsync(Event(tenant, SubscriptionStatus.Active, eventId: "evt_a"));
        await HandleAsync(Event(tenant, SubscriptionStatus.Active, eventId: "evt_b")); // still active

        await using var read = Fixture.CreateContext(tenant);
        Assert.Empty(await read.Set<Notification>().Where(n => n.UserId == ownerId).ToListAsync());
    }

    // --- recency guard (v2 audit LOGIC-B1): out-of-order/redelivered events must not regress state ---

    [Fact]
    public async Task StaleOutOfOrderEvent_DoesNotClobberNewerStatus()
    {
        var tenant = Guid.CreateVersion7();
        var older = EventEpoch.AddMinutes(10);
        var newer = EventEpoch.AddMinutes(20);

        // The newer 'active' event lands first: the tenant is entitled.
        Assert.Equal(WebhookResult.Applied, await HandleAsync(Event(tenant, SubscriptionStatus.Active, "evt_new", newer)));
        Assert.True(await EntitledAsync(tenant));

        // A stale, out-of-order 'canceled' (older emission, distinct event id) must be ignored — not applied.
        Assert.Equal(WebhookResult.Ignored, await HandleAsync(Event(tenant, SubscriptionStatus.Canceled, "evt_stale", older)));

        await using var read = Fixture.CreateContext(tenant);
        Assert.Equal(SubscriptionStatus.Active, (await read.Set<Subscription>().SingleAsync()).Status); // unchanged
        Assert.True(await EntitledAsync(tenant)); // still entitled
    }

    [Fact]
    public async Task SameSecond_DistinctEvents_BothApply_NewerNotDroppedAsStale()
    {
        // v3 LB-BILL-1: Stripe's Created is whole-second, so two DISTINCT events in the same second must
        // both apply — the recency guard rejects only STRICTLY older (<), not <=. Exact redelivery is caught
        // by the inbox (by EventId), so this can't double-apply the same event.
        var tenant = Guid.CreateVersion7();
        var sameInstant = EventEpoch.AddMinutes(10);

        Assert.Equal(WebhookResult.Applied, await HandleAsync(Event(tenant, SubscriptionStatus.Trialing, "evt_created", sameInstant)));
        Assert.Equal(WebhookResult.Applied, await HandleAsync(Event(tenant, SubscriptionStatus.Active, "evt_updated", sameInstant))); // same second → still applies

        await using var read = Fixture.CreateContext(tenant);
        Assert.Equal(SubscriptionStatus.Active, (await read.Set<Subscription>().SingleAsync()).Status); // newer won, not dropped
        Assert.True(await EntitledAsync(tenant));
    }

    [Fact]
    public async Task StaleEvent_DoesNotReNotifyDunning()
    {
        var tenant = Guid.CreateVersion7();
        var ownerId = await SeedOwnerAsync(tenant);
        var t1 = EventEpoch.AddMinutes(10);
        var t2 = EventEpoch.AddMinutes(20);

        await HandleAsync(Event(tenant, SubscriptionStatus.Active, "evt_a", t1));
        await HandleAsync(Event(tenant, SubscriptionStatus.PastDue, "evt_b", t2)); // real transition → notify once

        // A stale past_due redelivery (older, distinct id) must not fire a second dunning notification.
        await HandleAsync(Event(tenant, SubscriptionStatus.PastDue, "evt_stale", t1));

        await using var read = Fixture.CreateContext(tenant);
        Assert.Single(await read.Set<Notification>().Where(n => n.UserId == ownerId).ToListAsync());
    }

    // --- helpers ---

    private static int _seq;
    private static readonly DateTimeOffset EventEpoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Each call gets a strictly-increasing <c>OccurredAt</c> by default (so a later-constructed event is
    /// "newer", matching provider ordering and the recency guard); pass <paramref name="occurredAt"/> to
    /// simulate a stale/out-of-order delivery.
    /// </summary>
    private static BillingWebhookEvent Event(Guid tenant, string status, string eventId = "evt_default", DateTimeOffset? occurredAt = null) =>
        new(eventId, tenant, PlanKeys.Pro, status, "cus_1", "sub_1", DateTimeOffset.UtcNow.AddDays(30),
            occurredAt ?? EventEpoch.AddSeconds(Interlocked.Increment(ref _seq)));

    /// <summary>Fresh context+handler per call (a webhook delivery is its own scope); DB is shared.</summary>
    private async Task<WebhookResult> HandleAsync(BillingWebhookEvent evt, string signature = FakeBillingProvider.ValidSignature)
    {
        var current = new HttpCurrentTenant(new HttpContextAccessor()); // no JWT
        await using var db = NewContext(current);
        var handler = new BillingWebhookHandler(
            new FakeBillingProvider(),
            new EfInbox(db, TimeProvider.System),
            new EfRepository<Subscription>(db),
            current,
            new EfUnitOfWork(db),
            BuildNotifier(db),
            TimeProvider.System);

        return await handler.HandleAsync(JsonSerializer.Serialize(evt), signature, default);
    }

    private static IBillingNotifier BuildNotifier(AppDbContext db) =>
        new BillingNotifier(
            new TenantRepository(db),
            new NotificationService(
                new EfRepository<Notification>(db), new EfRepository<NotificationPreference>(db),
                new UserRepository(db), new NoopEmailSender(), TimeProvider.System));

    private async Task<Guid> SeedOwnerAsync(Guid tenant)
    {
        var ownerId = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);
        db.Set<Tenant>().Add(new Tenant { Id = tenant, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
        db.Set<User>().Add(new User { Id = ownerId, Email = $"owner-{ownerId:N}@x.com" });
        db.Set<TenantMembership>().Add(new TenantMembership { TenantId = tenant, UserId = ownerId, Role = TenantRoles.Owner });
        await db.SaveChangesAsync();
        return ownerId;
    }

    private async Task<bool> EntitledAsync(Guid tenant)
    {
        await using var db = Fixture.CreateContext(tenant);
        return await new EntitlementService(new EfRepository<Subscription>(db), TimeProvider.System)
            .HasAsync(Entitlements.ProFeature);
    }

    private AppDbContext NewContext(ICurrentTenant currentTenant) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Fixture.ConnectionString).Options, currentTenant);
}
