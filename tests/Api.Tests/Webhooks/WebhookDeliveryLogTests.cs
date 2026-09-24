using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Outbox;
using Vuelto.Infrastructure.Repositories;
using Vuelto.Infrastructure.Webhooks;

namespace Vuelto.Api.Tests.Webhooks;

/// <summary>
/// Drives HOOKS-2 (ADR-016): the delivery log + replay. The outbox handler records one
/// <see cref="WebhookDelivery"/> per attempt: a SUCCESS row is staged on the shared context (it
/// commits atomically with the message's Sent flip), while a FAILED attempt is written through a
/// fresh out-of-band context — the processor rolls the ambient transaction back on failure, so a
/// staged row would be silently discarded and the log would only ever show successes (the
/// 2026-08-24 finding: an operator diagnosing a failing endpoint saw an empty log).
/// The read side is tenant-scoped; replay re-enqueues the exact stored payload.
/// </summary>
[Collection(PostgresCollection.Name)]
public class WebhookDeliveryLogTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task Handler_RecordsDelivery_OnSuccess()
    {
        var tenant = Guid.CreateVersion7();
        var protector = new WebhookSecretProtector(new EphemeralDataProtectionProvider());
        var subId = await SeedSubscriptionAsync(tenant, protector);

        await using (var db = Fixture.CreateContext())
        {
            var handler = new WebhookOutboxHandler(db, new WebhookSender(new HttpClient(new StubHandler(HttpStatusCode.OK)), new AllowAllUrlGuard()), protector, TimeProvider.System, Fixture.CreateContextFactory());
            await handler.HandleAsync(Message(tenant, subId, "{}"), default);
            await db.SaveChangesAsync(); // stands in for the OutboxProcessor's commit
        }

        await using var read = Fixture.CreateContext();
        var delivery = Assert.Single(await read.Set<WebhookDelivery>().ToListAsync());
        Assert.True(delivery.Success);
        Assert.Equal(200, delivery.StatusCode);
        Assert.Equal(tenant, delivery.TenantId);
    }

    [Fact]
    public async Task Handler_RecordsDelivery_OnFailure_WithoutTheAmbientContextEverSaving()
    {
        var tenant = Guid.CreateVersion7();
        var protector = new WebhookSecretProtector(new EphemeralDataProtectionProvider());
        var subId = await SeedSubscriptionAsync(tenant, protector);

        await using (var db = Fixture.CreateContext())
        {
            var handler = new WebhookOutboxHandler(db, new WebhookSender(new HttpClient(new StubHandler(HttpStatusCode.InternalServerError)), new AllowAllUrlGuard()), protector, TimeProvider.System, Fixture.CreateContextFactory());
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(Message(tenant, subId, "{}"), default));
            // Deliberately NO SaveChanges here: in production the processor ROLLS BACK on failure.
            // The row must already be durable regardless.
        }

        await using var read = Fixture.CreateContext();
        var delivery = Assert.Single(await read.Set<WebhookDelivery>().ToListAsync());
        Assert.False(delivery.Success);
        Assert.Equal(500, delivery.StatusCode);
        Assert.NotNull(delivery.Error);
        Assert.Equal(tenant, delivery.TenantId);
    }

    [Fact]
    public async Task FailedDelivery_SurvivesTheProcessorRollback_AndRetries()
    {
        // The end-to-end path the old unit test missed: the REAL OutboxProcessor claims the message,
        // the handler fails, the processor rolls back and clears the tracker — the failed-attempt
        // row must survive all of that, and the message must be scheduled for retry.
        var tenant = Guid.CreateVersion7();
        var protector = new WebhookSecretProtector(new EphemeralDataProtectionProvider());
        var subId = await SeedSubscriptionAsync(tenant, protector);
        await SeedOutboxMessageAsync(tenant, subId);

        await using (var db = Fixture.CreateContext())
        {
            var handler = new WebhookOutboxHandler(db, new WebhookSender(new HttpClient(new StubHandler(HttpStatusCode.InternalServerError)), new AllowAllUrlGuard()), protector, TimeProvider.System, Fixture.CreateContextFactory());
            await new OutboxProcessor(db, [handler], TimeProvider.System, new OutboxOptions(), NullLogger<OutboxProcessor>.Instance).ProcessDueAsync();
        }

        await using var read = Fixture.CreateContext();
        var delivery = Assert.Single(await read.Set<WebhookDelivery>().ToListAsync());
        Assert.False(delivery.Success);
        Assert.Equal(500, delivery.StatusCode);

        var msg = await read.Set<OutboxMessage>().SingleAsync();
        Assert.Equal(OutboxStatus.Pending, msg.Status); // scheduled for retry, not lost
        Assert.Equal(1, msg.AttemptCount);
    }

    [Fact]
    public async Task DeadLetteredDelivery_KeepsOneRowPerAttempt()
    {
        // "Retries add rows" (DATA_MODEL.md): a delivery that exhausts its attempts leaves the full
        // per-attempt trail — the exact evidence an operator needs when an endpoint is down.
        var tenant = Guid.CreateVersion7();
        var protector = new WebhookSecretProtector(new EphemeralDataProtectionProvider());
        var subId = await SeedSubscriptionAsync(tenant, protector);
        await SeedOutboxMessageAsync(tenant, subId);
        var options = new OutboxOptions { MaxAttempts = 2, BackoffBase = TimeSpan.Zero };

        await using (var db = Fixture.CreateContext())
        {
            var handler = new WebhookOutboxHandler(db, new WebhookSender(new HttpClient(new StubHandler(HttpStatusCode.InternalServerError)), new AllowAllUrlGuard()), protector, TimeProvider.System, Fixture.CreateContextFactory());
            await new OutboxProcessor(db, [handler], TimeProvider.System, options, NullLogger<OutboxProcessor>.Instance).ProcessDueAsync();
        }

        await using var read = Fixture.CreateContext();
        Assert.Equal(2, await read.Set<WebhookDelivery>().CountAsync(d => !d.Success));
        Assert.Equal(OutboxStatus.DeadLettered, (await read.Set<OutboxMessage>().SingleAsync()).Status);
    }

    // --- synchronous "send test" also records a delivery (HOOKS-2) ---
    // The test-send is the only in-template path that actually fires a delivery, so it must log one row
    // per attempt too — otherwise the delivery log / replay are unreachable on the shipped template.

    [Fact]
    public async Task SendTest_RecordsDelivery_OnSuccess()
    {
        var tenant = Guid.CreateVersion7();
        var protector = new WebhookSecretProtector(new EphemeralDataProtectionProvider());
        var subId = await SeedSubscriptionAsync(tenant, protector);

        WebhookTestResult? result;
        await using (var db = Fixture.CreateContext(tenant))
        {
            var sender = new WebhookSender(new HttpClient(new StubHandler(HttpStatusCode.OK)), new AllowAllUrlGuard());
            result = await BuildService(db, tenant, protector, sender).SendTestAsync(subId, default);
        }

        Assert.NotNull(result);
        Assert.True(result!.Delivered);
        Assert.Equal(200, result.StatusCode);
        Assert.False(result.TransportFailed);

        await using var read = Fixture.CreateContext();
        var delivery = Assert.Single(await read.Set<WebhookDelivery>().ToListAsync());
        Assert.True(delivery.Success);
        Assert.Equal(200, delivery.StatusCode);
        Assert.Equal(tenant, delivery.TenantId);
        Assert.Equal(subId, delivery.SubscriptionId);
        Assert.Equal(WebhookEvents.Ping, delivery.EventType);
    }

    [Fact]
    public async Task SendTest_RecordsDelivery_OnNon2xx()
    {
        var tenant = Guid.CreateVersion7();
        var protector = new WebhookSecretProtector(new EphemeralDataProtectionProvider());
        var subId = await SeedSubscriptionAsync(tenant, protector);

        WebhookTestResult? result;
        await using (var db = Fixture.CreateContext(tenant))
        {
            var sender = new WebhookSender(new HttpClient(new StubHandler(HttpStatusCode.InternalServerError)), new AllowAllUrlGuard());
            result = await BuildService(db, tenant, protector, sender).SendTestAsync(subId, default);
        }

        Assert.NotNull(result);
        Assert.False(result!.Delivered);
        Assert.Equal(500, result.StatusCode);
        Assert.False(result.TransportFailed); // a returned HTTP status is not a transport failure

        await using var read = Fixture.CreateContext();
        var delivery = Assert.Single(await read.Set<WebhookDelivery>().ToListAsync());
        Assert.False(delivery.Success);
        Assert.Equal(500, delivery.StatusCode);
    }

    [Fact]
    public async Task SendTest_RecordsDelivery_OnTransportFailure()
    {
        var tenant = Guid.CreateVersion7();
        var protector = new WebhookSecretProtector(new EphemeralDataProtectionProvider());
        var subId = await SeedSubscriptionAsync(tenant, protector);

        WebhookTestResult? result;
        await using (var db = Fixture.CreateContext(tenant))
        {
            var sender = new WebhookSender(new HttpClient(new ThrowingHandler()), new AllowAllUrlGuard());
            result = await BuildService(db, tenant, protector, sender).SendTestAsync(subId, default);
        }

        Assert.NotNull(result);
        Assert.False(result!.Delivered);
        Assert.Null(result.StatusCode);
        Assert.True(result.TransportFailed);

        await using var read = Fixture.CreateContext();
        var delivery = Assert.Single(await read.Set<WebhookDelivery>().ToListAsync());
        Assert.False(delivery.Success);
        Assert.Null(delivery.StatusCode);
        Assert.False(string.IsNullOrEmpty(delivery.Error)); // failure detail retained for the debug trail
    }

    [Fact]
    public async Task SendTest_UnknownOrOtherTenantSubscription_ReturnsNull_SendsAndRecordsNothing()
    {
        // B's subscription is real and A knows its id. Test-sending it as A must look like an id that doesn't
        // exist (null -> 404, never 403, which would confirm the id) — and must not POST a ping signed with B's
        // secret to B's URL, nor log a delivery (TB-TEN-17).
        var protector = new WebhookSecretProtector(new EphemeralDataProtectionProvider());
        var pair = await TwoTenants.SeedAsync(t => SeedSubscriptionAsync(t, protector));
        var endpoint = new RecordingHandler();

        await using (var db = Fixture.CreateContext(pair.Mine))
        {
            var service = BuildService(db, pair.Mine, protector, new WebhookSender(new HttpClient(endpoint), new AllowAllUrlGuard()));
            Assert.Null(await service.SendTestAsync(pair.OtherSeed, default));
            Assert.Null(await service.SendTestAsync(Guid.CreateVersion7(), default)); // and an id that exists nowhere
            Assert.Equal(0, endpoint.Calls);
            await using (var read = Fixture.CreateContext())
                Assert.Empty(await read.Set<WebhookDelivery>().ToListAsync());

            // Control: A's own subscription does send — the nulls above are the tenant wall, not a broken send.
            Assert.NotNull(await service.SendTestAsync(pair.MineSeed, default));
            Assert.Equal(1, endpoint.Calls);
        }
    }

    [Fact]
    public async Task Replay_ReenqueuesTheSamePayload()
    {
        var tenant = Guid.CreateVersion7();
        var deliveryId = await SeedDeliveryAsync(tenant, subscriptionId: Guid.CreateVersion7(), eventId: "evt-42", body: "{\"n\":1}");

        await using (var db = Fixture.CreateContext(tenant))
            Assert.True(await BuildService(db, tenant).ReplayAsync(deliveryId, default));

        await using var read = Fixture.CreateContext();
        var message = Assert.Single(await read.Set<OutboxMessage>().Where(m => m.Type == WebhookOutboxHandler.MessageType).ToListAsync());
        var payload = JsonSerializer.Deserialize<WebhookOutboxPayload>(message.Payload)!;
        Assert.Equal("evt-42", payload.EventId);
        Assert.Equal("{\"n\":1}", payload.Body);
    }

    [Fact]
    public async Task Replay_UnknownOrOtherTenant_ReturnsFalse_AndQueuesNothing()
    {
        // B's delivery is real and A knows its id. Replaying it as A must look like an id that doesn't exist
        // (false -> 404, never 403) and queue nothing: a queued replay would send B's payload, signed with B's
        // secret, to B's endpoint on A's say-so. WebhookDelivery isn't ITenantScoped, so the only wall is the
        // service's own TenantId filter — this is the test that fails if it goes (TB-TEN-16).
        var pair = await TwoTenants.SeedAsync(t => SeedDeliveryAsync(t, Guid.CreateVersion7(), $"evt-{t}", "{}"));

        await using (var db = Fixture.CreateContext(pair.Mine))
        {
            Assert.False(await BuildService(db, pair.Mine).ReplayAsync(pair.OtherSeed, default));
            Assert.False(await BuildService(db, pair.Mine).ReplayAsync(Guid.CreateVersion7(), default)); // and an id that exists nowhere
        }
        await using (var read = Fixture.CreateContext())
            Assert.Empty(await read.Set<OutboxMessage>().ToListAsync());

        // Control: A's own delivery does replay — the falses above are the tenant wall, not a broken replay.
        await using (var db = Fixture.CreateContext(pair.Mine))
            Assert.True(await BuildService(db, pair.Mine).ReplayAsync(pair.MineSeed, default));
        await using var after = Fixture.CreateContext();
        var queued = Assert.Single(await after.Set<OutboxMessage>().ToListAsync());
        Assert.Equal(pair.Mine, queued.TenantId);
    }

    [Fact]
    public async Task ListDeliveries_IsTenantScoped()
    {
        var sub = Guid.CreateVersion7(); // the same subscription id in both tenants
        var pair = await TwoTenants.SeedAsync(t => SeedDeliveryAsync(t, sub, $"evt-{t}", "{}"));

        await using var db = Fixture.CreateContext(pair.Mine);
        var list = await BuildService(db, pair.Mine).ListDeliveriesAsync(sub, default);
        Assert.Equal(pair.MineSeed, Assert.Single(list).Id);
    }

    // --- helpers ---

    private static OutboxMessage Message(Guid tenant, Guid subId, string body) => new()
    {
        Type = WebhookOutboxHandler.MessageType,
        TenantId = tenant,
        Payload = JsonSerializer.Serialize(new WebhookOutboxPayload(subId, "ping", "e1", body)),
        CreatedAt = DateTimeOffset.UtcNow,
        NextAttemptAt = DateTimeOffset.UtcNow,
    };

    private static WebhookSubscriptionService BuildService(
        Vuelto.Infrastructure.Persistence.AppDbContext db, Guid tenant,
        WebhookSecretProtector? protector = null, IWebhookSender? sender = null) =>
        new(new EfRepository<WebhookSubscription>(db), new EfRepository<WebhookDelivery>(db),
            new EfOutbox(db, TimeProvider.System), new TestCurrentTenant { TenantId = tenant },
            new TokenGenerator(), protector ?? new WebhookSecretProtector(new EphemeralDataProtectionProvider()),
            sender ?? new WebhookSender(new HttpClient(new StubHandler(HttpStatusCode.OK)), new AllowAllUrlGuard()),
            new AllowAllUrlGuard(), TimeProvider.System);

    private async Task<Guid> SeedSubscriptionAsync(Guid tenant, WebhookSecretProtector protector)
    {
        var sub = new WebhookSubscription
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            Url = "https://recv.test/h",
            EventTypes = "ping",
            EncryptedSecret = protector.Protect("whsec_" + Guid.NewGuid().ToString("N")),
            CreatedByUserId = Guid.CreateVersion7(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await using var db = Fixture.CreateContext(tenant);
        db.Set<WebhookSubscription>().Add(sub);
        await db.SaveChangesAsync();
        return sub.Id;
    }

    private async Task SeedOutboxMessageAsync(Guid tenant, Guid subId)
    {
        await using var db = Fixture.CreateContext();
        db.Set<OutboxMessage>().Add(Message(tenant, subId, "{}"));
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedDeliveryAsync(Guid tenant, Guid subscriptionId, string eventId, string body)
    {
        var delivery = new WebhookDelivery
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            SubscriptionId = subscriptionId,
            EventType = "ping",
            EventId = eventId,
            Body = body,
            Success = false,
            StatusCode = 500,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await using var db = Fixture.CreateContext(); // not ITenantScoped — no ambient tenant needed
        db.Set<WebhookDelivery>().Add(delivery);
        await db.SaveChangesAsync();
        return delivery.Id;
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused"); // stands in for a transport/DNS failure
    }
}
