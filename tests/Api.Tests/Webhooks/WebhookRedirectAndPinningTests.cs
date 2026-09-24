using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Http;
using Vuelto.Infrastructure.Outbox;
using Vuelto.Infrastructure.Repositories;
using Vuelto.Infrastructure.Webhooks;

namespace Vuelto.Api.Tests.Webhooks;

/// <summary>
/// v4 audit H8 (T37: LB-JOBS-1/2/3, JOBS-9, ADV-P4-9; R130, R57 amendment), decision #7. The webhook client was
/// registered with only a timeout, so it followed redirects: a 301/302/303 became a body-less GET to a host the
/// SSRF guard never checked, whose 200 was then recorded as a delivered event (never delivered, never retried),
/// and a 307 carried the signed body on to it. The guard resolved DNS once and the socket resolved again — a
/// rebinding window. And a guard refusal was retried five times. Now the client follows no redirect (a 3xx is a
/// failed delivery that says why), dials only an address the guard accepts at connect time, and a refusal
/// dead-letters on the first attempt.
/// </summary>
[Collection(PostgresCollection.Name)]
public class WebhookRedirectAndPinningTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Theory]
    [InlineData(302, false)]
    [InlineData(307, false)] // a 307 would re-send the signed body to the new location
    [InlineData(302, true)]  // to the cloud metadata address
    public async Task Sender_ReturnsTheRedirect_AndNeverFollowsIt(int status, bool toMetadata)
    {
        await using var target = new TinyHttpServer(_ => "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        var location = toMetadata ? "http://169.254.169.254/latest/meta-data/" : $"http://127.0.0.1:{target.Port}/elsewhere";
        await using var redirector = new TinyHttpServer(_ =>
            $"HTTP/1.1 {status} Moved\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

        var guard = new OutboundUrlGuard(new FakeHostEnvironment(Environments.Development)); // loopback allowed locally
        using var client = new HttpClient(WebhookHttp.CreatePrimaryHandler(guard)) { Timeout = TimeSpan.FromSeconds(5) };

        var returned = await new WebhookSender(client, guard).SendAsync($"http://127.0.0.1:{redirector.Port}/h", "whsec_x", "ping", "e1", "{}");

        Assert.Equal(status, returned);
        Assert.Equal(1, redirector.Requests);
        Assert.Equal(0, target.Requests);
    }

    [Fact]
    public async Task Sender_ConnectsOnlyToAnAddressTheGuardAcceptsAtConnectTime()
    {
        // The name resolves to a public address when the guard checks the URL, and to loopback a moment later when
        // the socket would dial it — DNS rebinding. The connection must re-check what it actually dials.
        var answers = new Queue<IPAddress[]>([[IPAddress.Parse("203.0.113.10")], [IPAddress.Loopback]]);
        var guard = new OutboundUrlGuard(new FakeHostEnvironment(Environments.Production),
            (_, _) => Task.FromResult(answers.Count > 1 ? answers.Dequeue() : answers.Peek()));
        using var client = new HttpClient(WebhookHttp.CreatePrimaryHandler(guard)) { Timeout = TimeSpan.FromSeconds(5) };

        await Assert.ThrowsAsync<WebhookUrlRefusedException>(() =>
            new WebhookSender(client, guard).SendAsync("https://rebind.test/h", "whsec_x", "ping", "e1", "{}"));
    }

    [Fact]
    public void WebhookClient_FollowsNoRedirects_PinsItsConnection_AndIsRegisteredThatWay()
    {
        var handler = WebhookHttp.CreatePrimaryHandler(new AllowAllUrlGuard());
        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);
        Assert.False(handler.UseProxy); // a proxy would resolve the host itself, past the connect-time check

        var registration = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Infrastructure", "ServiceCollectionExtensions.cs"));
        Assert.Matches(@"AddHttpClient<IWebhookSender, WebhookSender>\([^;]*ConfigurePrimaryHttpMessageHandler\([^;]*WebhookHttp\.CreatePrimaryHandler", registration);
    }

    [Theory]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    public async Task Handler_3xx_IsRecordedAsAFailureThatSaysWhy(HttpStatusCode status)
    {
        var tenant = Guid.CreateVersion7();
        var protector = new WebhookSecretProtector(new EphemeralDataProtectionProvider());
        var subId = await SeedSubscriptionAsync(tenant, protector);

        await using (var db = Fixture.CreateContext())
        {
            var handler = new WebhookOutboxHandler(db, new WebhookSender(new HttpClient(new StatusHandler(status)), new AllowAllUrlGuard()),
                protector, TimeProvider.System, Fixture.CreateContextFactory());
            await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(Message(tenant, subId), default));
        }

        await using var read = Fixture.CreateContext();
        var delivery = Assert.Single(await read.Set<WebhookDelivery>().ToListAsync());
        Assert.False(delivery.Success);
        Assert.Equal((int)status, delivery.StatusCode);
        Assert.Contains("redirect", delivery.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendTest_302_IsNotDelivered_AndSaysWhy()
    {
        var tenant = Guid.CreateVersion7();
        var protector = new WebhookSecretProtector(new EphemeralDataProtectionProvider());
        var subId = await SeedSubscriptionAsync(tenant, protector);

        await using (var db = Fixture.CreateContext(tenant))
        {
            var result = await Service(db, tenant, protector, new WebhookSender(new HttpClient(new StatusHandler(HttpStatusCode.Found)), new AllowAllUrlGuard()))
                .SendTestAsync(subId, default);
            Assert.False(result!.Delivered);
            Assert.Equal(302, result.StatusCode);
        }

        await using var read = Fixture.CreateContext();
        Assert.Contains("redirect", Assert.Single(await read.Set<WebhookDelivery>().ToListAsync()).Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GuardRefusal_DeadLettersOnTheFirstAttempt_WithOneDeliveryRow()
    {
        var tenant = Guid.CreateVersion7();
        var protector = new WebhookSecretProtector(new EphemeralDataProtectionProvider());
        var subId = await SeedSubscriptionAsync(tenant, protector);
        await using (var seed = Fixture.CreateContext())
        {
            seed.Set<OutboxMessage>().Add(Message(tenant, subId));
            await seed.SaveChangesAsync();
        }
        var endpoint = new StatusHandler(HttpStatusCode.OK);

        await using (var db = Fixture.CreateContext())
        {
            var handler = new WebhookOutboxHandler(db, new WebhookSender(new HttpClient(endpoint), new RefusingUrlGuard()),
                protector, TimeProvider.System, Fixture.CreateContextFactory());
            await new OutboxProcessor(db, [handler], TimeProvider.System, new OutboxOptions(), NullLogger<OutboxProcessor>.Instance)
                .ProcessNextAsync();
        }

        await using var read = Fixture.CreateContext();
        var message = await read.Set<OutboxMessage>().SingleAsync();
        Assert.Equal(OutboxStatus.DeadLettered, message.Status); // a refusal won't change on retry
        Assert.Equal(1, message.AttemptCount);
        Assert.StartsWith("url_refused", Assert.Single(await read.Set<WebhookDelivery>().ToListAsync()).Error);
        Assert.Equal(0, endpoint.Calls);
    }

    // --- helpers ---

    private static OutboxMessage Message(Guid tenant, Guid subId) => new()
    {
        Type = WebhookOutboxHandler.MessageType,
        TenantId = tenant,
        Payload = JsonSerializer.Serialize(new WebhookOutboxPayload(subId, "ping", "e1", "{}")),
        CreatedAt = DateTimeOffset.UtcNow,
        NextAttemptAt = DateTimeOffset.UtcNow,
    };

    private static WebhookSubscriptionService Service(Vuelto.Infrastructure.Persistence.AppDbContext db, Guid tenant,
        WebhookSecretProtector protector, IWebhookSender sender) =>
        new(new EfRepository<WebhookSubscription>(db), new EfRepository<WebhookDelivery>(db),
            new EfOutbox(db, TimeProvider.System), new TestCurrentTenant { TenantId = tenant },
            new TokenGenerator(), protector, sender, new AllowAllUrlGuard(), TimeProvider.System);

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

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root.");
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(status);
            if ((int)status is >= 300 and < 400)
                response.Headers.Location = new Uri("https://elsewhere.test/");
            return Task.FromResult(response);
        }
    }

    private sealed class RefusingUrlGuard : IOutboundUrlGuard
    {
        public ValueTask<bool> IsAllowedAsync(string? url, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

        public ValueTask<IPAddress[]> ResolveAllowedAsync(string host, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IPAddress[]>([]);
    }

    /// <summary>A loopback HTTP/1.1 server that answers every request with one canned response and counts them —
    /// enough to watch a real <see cref="SocketsHttpHandler"/> decide whether to follow a redirect.</summary>
    private sealed class TinyHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Func<string, string> _respond;
        private int _requests;

        public TinyHttpServer(Func<string, string> respond)
        {
            _respond = respond;
            _listener.Start();
            _ = AcceptAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Requests => Volatile.Read(ref _requests);

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                    _ = ServeAsync(await _listener.AcceptTcpClientAsync());
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private async Task ServeAsync(TcpClient connection)
        {
            using (connection)
            {
                var stream = connection.GetStream();
                var received = new List<byte>();
                var buffer = new byte[8192];
                int headerEnd;
                while ((headerEnd = IndexOf(received, "\r\n\r\n"u8.ToArray())) < 0)
                {
                    var n = await stream.ReadAsync(buffer);
                    if (n == 0) return;
                    received.AddRange(buffer.AsSpan(0, n).ToArray());
                }

                // Read the whole body before answering, so closing the socket never resets an unread request.
                var head = Encoding.ASCII.GetString([.. received.Take(headerEnd)]);
                var length = head.Split("\r\n").Select(l => l.Split(':', 2))
                    .Where(p => p.Length == 2 && p[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    .Select(p => int.Parse(p[1].Trim())).FirstOrDefault();
                while (received.Count - (headerEnd + 4) < length)
                {
                    var n = await stream.ReadAsync(buffer);
                    if (n == 0) break;
                    received.AddRange(buffer.AsSpan(0, n).ToArray());
                }

                Interlocked.Increment(ref _requests);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(_respond(head)));
                await stream.FlushAsync();
            }
        }

        private static int IndexOf(List<byte> haystack, byte[] needle)
        {
            for (var i = 0; i + needle.Length <= haystack.Count; i++)
                if (needle.Select((b, j) => haystack[i + j] == b).All(x => x))
                    return i;
            return -1;
        }

        public ValueTask DisposeAsync()
        {
            _listener.Stop();
            return ValueTask.CompletedTask;
        }
    }
}
