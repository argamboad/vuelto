using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Perezosoft.Api.Services;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Core.Entities;
using Perezosoft.Infrastructure.Outbox;
using Perezosoft.Infrastructure.Repositories;
using Perezosoft.Infrastructure.Webhooks;

namespace Perezosoft.Api.Tests.Webhooks;

/// <summary>
/// Drives HOOKS (ADR-016) management: create returns the signing secret once and stores it **encrypted**
/// (not plaintext); URLs and event types are validated; list is tenant-scoped; delete removes. Real Postgres.
/// </summary>
[Collection(PostgresCollection.Name)]
public class WebhookSubscriptionServiceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static readonly Guid Creator = Guid.CreateVersion7();

    [Fact]
    public async Task Create_ReturnsSecretOnce_AndStoresEncrypted()
    {
        var tenant = Guid.CreateVersion7();
        var protector = NewProtector();
        await using var db = Fixture.CreateContext(tenant);
        var svc = Build(db, protector);

        var created = await svc.CreateAsync(Creator, "https://example.test/hook", ["ping"], default);

        Assert.NotNull(created);
        Assert.StartsWith("whsec_", created!.Secret);
        Assert.NotEqual(created.Secret, created.Subscription.EncryptedSecret);        // not plaintext
        Assert.Equal(created.Secret, protector.Unprotect(created.Subscription.EncryptedSecret)); // decrypts back
        Assert.Equal("ping", created.Subscription.EventTypes);
        Assert.Equal(tenant, created.Subscription.TenantId);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.test")]
    public async Task Create_InvalidUrl_ReturnsNull(string url)
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);
        Assert.Null(await Build(db).CreateAsync(Creator, url, ["ping"], default));
    }

    [Fact]
    public async Task Create_NoEventTypes_DefaultsToKnown()
    {
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);

        var created = await Build(db).CreateAsync(Creator, "https://example.test/hook", null, default);

        Assert.NotNull(created);
        Assert.Contains(WebhookEvents.Ping, created!.Subscription.EventTypes);
    }

    [Fact]
    public async Task Create_AllInvalidEventTypes_IsRejected_NotSubscribedToAll()
    {
        // v2 audit SOLID-3: naming only unknown event types must be REJECTED, not silently subscribed to all.
        var tenant = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);

        var created = await Build(db).CreateAsync(Creator, "https://example.test/hook", ["bogus_event"], default);

        Assert.Null(created);
    }

    [Fact]
    public async Task List_IsTenantScoped()
    {
        var mine = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        await using (var db = Fixture.CreateContext(mine)) await Build(db).CreateAsync(Creator, "https://mine.test/h", ["ping"], default);
        await using (var db = Fixture.CreateContext(other)) await Build(db).CreateAsync(Creator, "https://theirs.test/h", ["ping"], default);

        await using var read = Fixture.CreateContext(mine);
        var list = await Build(read).ListAsync(default);
        Assert.Single(list);
        Assert.Equal("https://mine.test/h", list[0].Url);
    }

    [Fact]
    public async Task Delete_RemovesSubscription()
    {
        var tenant = Guid.CreateVersion7();
        Guid id;
        await using (var db = Fixture.CreateContext(tenant))
        {
            var created = await Build(db).CreateAsync(Creator, "https://example.test/h", ["ping"], default);
            id = created!.Subscription.Id;
        }

        await using (var db = Fixture.CreateContext(tenant))
            Assert.True(await Build(db).DeleteAsync(id, default));

        await using var read = Fixture.CreateContext(tenant);
        Assert.Empty(await read.Set<WebhookSubscription>().ToListAsync());
    }

    [Fact]
    public async Task Delete_CannotDeleteAnotherTenantsSubscription()
    {
        // v2 audit B8: write-side tenancy negative — a caller cannot delete a subscription owned by another tenant.
        var tenantB = Guid.CreateVersion7();
        Guid bSubId;
        await using (var db = Fixture.CreateContext(tenantB))
            bSubId = (await Build(db).CreateAsync(Creator, "https://b.test/h", ["ping"], default))!.Subscription.Id;

        var tenantA = Guid.CreateVersion7();
        await using (var db = Fixture.CreateContext(tenantA))
            Assert.False(await Build(db).DeleteAsync(bSubId, default)); // not found under tenant A → no-op

        await using var read = Fixture.CreateContext(tenantB);
        Assert.Single(await read.Set<WebhookSubscription>().ToListAsync()); // B's subscription survives
    }

    private static WebhookSecretProtector NewProtector() => new(new EphemeralDataProtectionProvider());

    private static WebhookSubscriptionService Build(Perezosoft.Infrastructure.Persistence.AppDbContext db, WebhookSecretProtector? protector = null) =>
        new(new EfRepository<WebhookSubscription>(db), new EfRepository<WebhookDelivery>(db),
            new EfOutbox(db, TimeProvider.System), new TestCurrentTenant(),
            new TokenGenerator(), protector ?? NewProtector(),
            new WebhookSender(new HttpClient(new UnusedHandler()), new AllowAllUrlGuard()), // send test not exercised here
            new AllowAllUrlGuard(), TimeProvider.System);

    private sealed class UnusedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("These tests do not send test webhooks.");
    }
}
