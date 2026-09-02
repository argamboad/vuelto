using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Webhooks;
using Vuelto.Infrastructure.Repositories;
using Vuelto.Infrastructure.Webhooks;

namespace Vuelto.Api.Tests.Webhooks;

/// <summary>
/// Drives HOOKS (ADR-016) delivery: the publisher fans an event out to one durable outbox message per
/// active matching subscription; the outbox handler signs + POSTs it, throwing on a non-2xx (so the outbox
/// retries) and no-op'ing if the subscription is gone.
/// </summary>
[Collection(PostgresCollection.Name)]
public class WebhookDeliveryTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    // --- publisher fan-out ---

    [Fact]
    public async Task Publish_EnqueuesOnePerActiveMatchingSubscription()
    {
        var tenant = Guid.CreateVersion7();
        await SeedSubscriptionAsync(tenant, "https://a.test/h", "ping");
        await SeedSubscriptionAsync(tenant, "https://b.test/h", "ping");
        await SeedSubscriptionAsync(tenant, "https://c.test/h", "other");            // wrong event
        await SeedSubscriptionAsync(tenant, "https://d.test/h", "ping", disabled: true); // inactive

        var outbox = new RecordingOutbox();
        await using (var db = Fixture.CreateContext(tenant))
            await new WebhookPublisher(new EfRepository<WebhookSubscription>(db), outbox,
                new TestCurrentTenant { TenantId = tenant }, TimeProvider.System)
                .PublishAsync(WebhookEvents.Ping, new { hello = "world" }, default);

        Assert.Equal(2, outbox.Messages.Count);
        Assert.All(outbox.Messages, m => Assert.Equal(WebhookOutboxHandler.MessageType, m.Type));
    }

    [Fact]
    public async Task Publish_NoMatchingSubscription_EnqueuesNothing()
    {
        var tenant = Guid.CreateVersion7();
        await SeedSubscriptionAsync(tenant, "https://a.test/h", "other");

        var outbox = new RecordingOutbox();
        await using (var db = Fixture.CreateContext(tenant))
            await new WebhookPublisher(new EfRepository<WebhookSubscription>(db), outbox,
                new TestCurrentTenant { TenantId = tenant }, TimeProvider.System)
                .PublishAsync(WebhookEvents.Ping, new { }, default);

        Assert.Empty(outbox.Messages);
    }

    // --- outbox handler delivery ---

    [Fact]
    public async Task Handler_PostsSignedBody()
    {
        var tenant = Guid.CreateVersion7();
        var protector = new WebhookSecretProtector(new EphemeralDataProtectionProvider());
        var (subId, secret) = await SeedSubscriptionAsync(tenant, "https://recv.test/h", "ping", protector: protector);

        var stub = new CapturingHandler(HttpStatusCode.OK);
        const string body = "{\"id\":\"e1\",\"type\":\"ping\"}";
        var message = OutboxMessage(new WebhookOutboxPayload(subId, "ping", "e1", body));

        await using var db = Fixture.CreateContext();
        await new WebhookOutboxHandler(db, new WebhookSender(new HttpClient(stub), new AllowAllUrlGuard()), protector, TimeProvider.System, Fixture.CreateContextFactory()).HandleAsync(message, default);

        Assert.NotNull(stub.LastRequest);
        Assert.Equal(body, stub.LastBody);
        Assert.Equal(WebhookSignature.Compute(secret, body), stub.Header(WebhookSignature.HeaderName));
        Assert.Equal("ping", stub.Header(WebhookSignature.EventHeaderName));
        Assert.Equal("e1", stub.Header(WebhookSignature.IdHeaderName));
    }

    [Fact]
    public async Task Handler_ThrowsOnNon2xx_SoOutboxRetries()
    {
        var tenant = Guid.CreateVersion7();
        var protector = new WebhookSecretProtector(new EphemeralDataProtectionProvider());
        var (subId, _) = await SeedSubscriptionAsync(tenant, "https://recv.test/h", "ping", protector: protector);

        var stub = new CapturingHandler(HttpStatusCode.InternalServerError);
        var message = OutboxMessage(new WebhookOutboxPayload(subId, "ping", "e1", "{}"));

        await using var db = Fixture.CreateContext();
        var handler = new WebhookOutboxHandler(db, new WebhookSender(new HttpClient(stub), new AllowAllUrlGuard()), protector, TimeProvider.System, Fixture.CreateContextFactory());
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(message, default));
    }

    [Fact]
    public async Task Handler_MissingSubscription_IsNoOp()
    {
        var stub = new CapturingHandler(HttpStatusCode.OK);
        var message = OutboxMessage(new WebhookOutboxPayload(Guid.CreateVersion7(), "ping", "e1", "{}"));

        await using var db = Fixture.CreateContext();
        await new WebhookOutboxHandler(db, new WebhookSender(new HttpClient(stub), new AllowAllUrlGuard()),
            new WebhookSecretProtector(new EphemeralDataProtectionProvider()), TimeProvider.System, Fixture.CreateContextFactory()).HandleAsync(message, default);

        Assert.Null(stub.LastRequest); // never POSTed
    }

    // --- helpers ---

    private static OutboxMessage OutboxMessage(WebhookOutboxPayload payload) => new()
    {
        Type = WebhookOutboxHandler.MessageType,
        Payload = JsonSerializer.Serialize(payload),
        CreatedAt = DateTimeOffset.UtcNow,
        NextAttemptAt = DateTimeOffset.UtcNow,
    };

    private async Task<(Guid Id, string Secret)> SeedSubscriptionAsync(
        Guid tenant, string url, string eventTypes, bool disabled = false, WebhookSecretProtector? protector = null)
    {
        protector ??= new WebhookSecretProtector(new EphemeralDataProtectionProvider());
        var secret = "whsec_" + Guid.NewGuid().ToString("N");
        var sub = new WebhookSubscription
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            Url = url,
            EventTypes = eventTypes,
            EncryptedSecret = protector.Protect(secret),
            CreatedByUserId = Guid.CreateVersion7(),
            CreatedAt = DateTimeOffset.UtcNow,
            DisabledAt = disabled ? DateTimeOffset.UtcNow : null,
        };
        await using var db = Fixture.CreateContext(tenant);
        db.Set<WebhookSubscription>().Add(sub);
        await db.SaveChangesAsync();
        return (sub.Id, secret);
    }

    private sealed class RecordingOutbox : IOutbox
    {
        public List<(string Type, string Payload, Guid? TenantId)> Messages { get; } = [];

        public Task EnqueueAsync(string type, string payload, Guid? tenantId = null, CancellationToken cancellationToken = default)
        {
            Messages.Add((type, payload, tenantId));
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        public string? Header(string name) =>
            LastRequest is not null && LastRequest.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : null;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status);
        }
    }
}
