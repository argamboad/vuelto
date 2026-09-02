using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Gdpr;

/// <summary>
/// Behavioral proof for LB-TEN-1: the three contributors that close the tenant-teardown holes actually
/// (a) delete their tables' rows for the dissolved tenant, (b) leave a second tenant's rows untouched, and
/// (c) export identifying metadata only — never the API-key hash, the webhook signing secret, or the
/// delivery body. Pairs with the structural canary <c>EveryTenantOwnedEntity_IsWiredIntoTenantDissolution</c>.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TenantTeardownContributorTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task ApiKeys_Wipe_RemovesTargetTenantOnly_AndExportHasNoHash()
    {
        var target = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        await SeedApiKeyAsync(target, name: "CI deploy", keyHash: "HASH-SECRET-TARGET");
        await SeedApiKeyAsync(other, name: "other", keyHash: "HASH-SECRET-OTHER");

        await using (var db = Fixture.CreateContext(target))
        {
            var export = await new ApiKeyDataContributor(new EfRepository<ApiKey>(db)).ExportAsync(target);
            var json = JsonSerializer.Serialize(export);
            Assert.Contains("CI deploy", json);
            Assert.DoesNotContain("HASH-SECRET-TARGET", json); // key hash is never exported

            await new ApiKeyDataContributor(new EfRepository<ApiKey>(db)).WipeAsync(target);
        }

        await using var read = Fixture.CreateContext();
        Assert.Empty(await AllOf<ApiKey>(read).Where(k => k.TenantId == target).ToListAsync());
        Assert.Single(await AllOf<ApiKey>(read).Where(k => k.TenantId == other).ToListAsync());
    }

    [Fact]
    public async Task UsageCounters_Wipe_RemovesTargetTenantOnly_AndExportsCounts()
    {
        var target = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        await SeedUsageAsync(target, key: "export", period: "2026-07", count: 5);
        await SeedUsageAsync(other, key: "export", period: "2026-07", count: 9);

        await using (var db = Fixture.CreateContext(target))
        {
            var export = await new UsageCounterDataContributor(new EfRepository<UsageCounter>(db)).ExportAsync(target);
            Assert.Contains("2026-07", JsonSerializer.Serialize(export));

            await new UsageCounterDataContributor(new EfRepository<UsageCounter>(db)).WipeAsync(target);
        }

        await using var read = Fixture.CreateContext();
        Assert.Empty(await AllOf<UsageCounter>(read).Where(c => c.TenantId == target).ToListAsync());
        Assert.Single(await AllOf<UsageCounter>(read).Where(c => c.TenantId == other).ToListAsync());
    }

    [Fact]
    public async Task Webhooks_Wipe_RemovesSubscriptionsAndDeliveries_TargetOnly_ExportHasNoSecrets()
    {
        var target = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var targetSub = await SeedWebhookAsync(target, url: "https://target.example/hook",
            encryptedSecret: "ENC-SECRET-TARGET", deliveryBody: "{\"secret-ish\":\"BODY-TARGET\"}");
        await SeedWebhookAsync(other, url: "https://other.example/hook",
            encryptedSecret: "ENC-SECRET-OTHER", deliveryBody: "{}");

        await using (var db = Fixture.CreateContext(target))
        {
            var export = await Build(db).ExportAsync(target);
            var json = JsonSerializer.Serialize(export);
            Assert.Contains("https://target.example/hook", json);
            Assert.DoesNotContain("ENC-SECRET-TARGET", json); // signing secret is never exported
            Assert.DoesNotContain("BODY-TARGET", json);       // delivery body is never exported

            await Build(db).WipeAsync(target);
        }

        await using var read = Fixture.CreateContext();
        Assert.Empty(await AllOf<WebhookSubscription>(read).Where(s => s.TenantId == target).ToListAsync());
        Assert.Empty(await read.Set<WebhookDelivery>().Where(d => d.TenantId == target).ToListAsync());
        Assert.Single(await AllOf<WebhookSubscription>(read).Where(s => s.TenantId == other).ToListAsync());
        Assert.Single(await read.Set<WebhookDelivery>().Where(d => d.TenantId == other).ToListAsync());
        _ = targetSub; // subscription id retained for readability; assertions key off TenantId
    }

    // --- helpers ---

    private static WebhookDataContributor Build(AppDbContext db) =>
        new(new EfRepository<WebhookSubscription>(db), new EfRepository<WebhookDelivery>(db));

    // Cross-tenant read for assertions — the global filter is bypassed so both tenants' rows are visible.
    private static IQueryable<T> AllOf<T>(AppDbContext db) where T : class =>
        db.Set<T>().IgnoreQueryFilters();

    private async Task SeedApiKeyAsync(Guid tenant, string name, string keyHash)
    {
        await using var db = Fixture.CreateContext(tenant); // interceptor stamps TenantId
        db.Set<ApiKey>().Add(new ApiKey
        {
            Name = name, KeyHash = keyHash, Prefix = "pk_ab12cd", Scopes = "read,write",
            CreatedByUserId = Guid.CreateVersion7(), CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedUsageAsync(Guid tenant, string key, string period, int count)
    {
        await using var db = Fixture.CreateContext(tenant);
        db.Set<UsageCounter>().Add(new UsageCounter
        {
            Key = key, Period = period, Count = count, UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedWebhookAsync(Guid tenant, string url, string encryptedSecret, string deliveryBody)
    {
        var subId = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext(tenant);
        db.Set<WebhookSubscription>().Add(new WebhookSubscription
        {
            Id = subId, Url = url, EventTypes = WebhookEvents.Ping, EncryptedSecret = encryptedSecret,
            CreatedByUserId = Guid.CreateVersion7(), CreatedAt = DateTimeOffset.UtcNow,
        });
        // WebhookDelivery is not ITenantScoped — set TenantId explicitly (the interceptor doesn't stamp it).
        db.Set<WebhookDelivery>().Add(new WebhookDelivery
        {
            TenantId = tenant, SubscriptionId = subId, EventType = WebhookEvents.Ping,
            EventId = Guid.CreateVersion7().ToString("N"), Body = deliveryBody,
            Success = true, StatusCode = 200, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return subId;
    }
}
