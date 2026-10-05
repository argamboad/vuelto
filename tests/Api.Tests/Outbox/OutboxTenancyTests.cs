using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Billing;
using Vuelto.Infrastructure.Email;
using Vuelto.Infrastructure.Outbox;
using Vuelto.Infrastructure.Repositories;
using Vuelto.Infrastructure.Webhooks;

namespace Vuelto.Api.Tests.Outbox;

/// <summary>
/// v4 audit H6 (T22, JOBS-2, ADV-P4-7, R91/R145): the outbox is platform infrastructure, but what it carries is
/// often a household's content — an invitation, an export with its attachment, a webhook body. Emails were
/// enqueued with no tenant id and no dissolve path touched the table, so Phase 4 found a dissolved household's
/// document and recipient still sitting in the outbox. Now the email sender stamps the ambient tenant, and
/// <see cref="OutboxDataContributor"/> removes a dissolved tenant's rows of every type that dissolves with its
/// tenant. Which types do is each handler's own declaration: <c>billing.cancel</c> is queued BY the dissolve and
/// must still run afterwards, or Stripe keeps charging a household that no longer exists.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OutboxTenancyTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task TenantScopedEmailEnqueues_StampTheCurrentTenant_PreAuthOnesStayTenantless()
    {
        var household = Guid.CreateVersion7();
        await using (var db = Fixture.CreateContext(household))
            await new OutboxEmailSender(new EfOutbox(db, TimeProvider.System), db, new TestCurrentTenant { TenantId = household })
                .SendAsync("member@x.com", "Your export", "<p>attached</p>");

        // A sign-in code goes out before anyone has a tenant — nothing to stamp.
        await using (var db = Fixture.CreateContext())
            await new OutboxEmailSender(new EfOutbox(db, TimeProvider.System), db, new TestCurrentTenant())
                .SendAsync("someone@x.com", "Your code", "<p>123456</p>");

        await using var read = Fixture.CreateContext();
        var rows = await read.Set<OutboxMessage>().ToListAsync();
        Assert.Equal(household, rows.Single(m => m.Payload.Contains("member@x.com")).TenantId);
        Assert.Null(rows.Single(m => m.Payload.Contains("someone@x.com")).TenantId);
    }

    [Fact]
    public async Task OutboxMessage_Lifecycle_Dissolve_WipesTheTenantsRows_OtherTenantsIntact()
    {
        var pair = await TwoTenants.SeedAsync(SeedOutboxAsync);
        Guid broadcast;
        await using (var db = Fixture.CreateContext())
        {
            var row = Row(AdminBroadcastOutboxHandler.MessageType, tenant: null);
            db.Set<OutboxMessage>().Add(row);
            await db.SaveChangesAsync();
            broadcast = row.Id;
        }

        var ambient = new TestCurrentTenant();
        await using (var db = Fixture.CreateTestContext(ambient))
        {
            var contributor = new OutboxDataContributor(new EfRepository<OutboxMessage>(db), AllHandlers());
            Assert.False(await contributor.HasDataAsync(pair.Mine)); // plumbing, never "content" that blocks a join
            await using var tx = await db.Database.BeginTransactionAsync();
            await new TenantDissolutionService([contributor], new TenantRepository(db), ambient).DissolveAsync(pair.Mine);
            await tx.CommitAsync();
        }

        await using var read = Fixture.CreateContext();
        // The dissolved household keeps only the effect its own dissolve queued.
        Assert.Equal([BillingCancelOutboxHandler.MessageType],
            await read.Set<OutboxMessage>().Where(m => m.TenantId == pair.Mine).Select(m => m.Type).ToListAsync());
        // The other household, and the platform-wide broadcast, are untouched.
        Assert.Equal(3, await read.Set<OutboxMessage>().CountAsync(m => m.TenantId == pair.Other));
        Assert.True(await read.Set<OutboxMessage>().AnyAsync(m => m.Id == broadcast));
    }

    [Fact]
    public void EveryOutboxHandler_DeclaresWhetherItsRowsDissolveWithTheirTenant()
    {
        // Each handler says whether its rows go when their tenant is dissolved; nothing defaults. A new handler
        // fails here until it is listed with the answer and the reason.
        var expected = new Dictionary<string, bool>
        {
            [OutboxEmailSender.MessageType] = true,              // the household's mail: recipient, body, attachments
            [WebhookOutboxHandler.MessageType] = true,           // the household's event body, for a subscription the dissolve deletes
            [BillingCancelOutboxHandler.MessageType] = false,    // queued BY the dissolve: must still cancel the Stripe subscription
            [AdminBroadcastOutboxHandler.MessageType] = false,   // platform-wide; never any one household's
        };

        var declared = AllHandlers().ToDictionary(h => h.Type, h => h.DissolvesWithItsTenant);
        Assert.Equal(expected.OrderBy(kv => kv.Key), declared.OrderBy(kv => kv.Key));
    }

    [Fact]
    public async Task OutboxMessage_Lifecycle_ExportExclusion_TheHouseholdExportCarriesNoQueueRows()
    {
        // A queue is not the household's data; its effects are. The export must stay empty even when the queue
        // holds the household's mail, or a "what is pending" export would hand out recipients and attachments.
        var pair = await TwoTenants.SeedAsync(SeedOutboxAsync);
        await using var db = Fixture.CreateContext();
        var contributor = new OutboxDataContributor(new EfRepository<OutboxMessage>(db), AllHandlers());

        Assert.True(await db.Set<OutboxMessage>().AnyAsync(m => m.TenantId == pair.Mine)); // there IS something to leak
        Assert.Null(await contributor.ExportAsync(pair.Mine));
    }

    [Fact]
    public void OutboxMessage_Lifecycle_TenantlessOrigins_AreOnlyTheNamedOnes()
    {
        // Which code may write a row with no tenant is a list, not a habit (R145). Every enqueue call site in src/
        // is named here with the tenant it stamps; a new call site fails until it is added with its rule. Two
        // origins may be tenant-less: mail sent before anyone has a tenant (a sign-in code — the sender stamps
        // the ambient tenant, which is null there) and the platform-wide broadcast, which is no one household's.
        var expected = new Dictionary<string, string>
        {
            ["src/Infrastructure/Email/OutboxEmailSender.cs"] = "currentTenant.TenantId", // ambient: null only pre-auth
            ["src/Api/Controllers/AdminController.cs"] = "tenantId: null",                // the broadcast
            ["src/Api/Services/BillingDataContributor.cs"] = "tenantId",                  // the tenant being dissolved
            ["src/Api/Services/WebhookService.cs"] = "tenantId",                          // the subscribing tenant
        };

        var root = RepoRoot();
        var found = new Dictionary<string, List<string>>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (rel is "src/Core/Abstractions/IOutbox.cs" or "src/Infrastructure/Outbox/EfOutbox.cs") continue; // the declaration and its implementation
            // The call's argument list ends at the first ");" — the tenant is the argument before the cancellation token.
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\.EnqueueAsync\((?<args>[\s\S]*?)\);"))
            {
                var args = m.Groups["args"].Value;
                var tenant = Regex.Match(args, @",\s*(?<t>tenantId: null|[A-Za-z_.]+),\s*cancellationToken\s*$").Groups["t"].Value;
                found.TryAdd(rel, []);
                found[rel].Add(tenant);
            }
        }

        Assert.Equal(expected.Keys.Order(), found.Keys.Order());
        foreach (var (file, tenants) in found)
            Assert.All(tenants, t => Assert.True(t == expected[file], $"{file} enqueues with tenant '{t}', expected '{expected[file]}' — name the origin's rule here"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.EnumerateFiles("*.slnx").Any()) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    /// <summary>Every <see cref="IOutboxHandler"/> the platform ships, found by reflection so a new one can't be
    /// missed; built without their constructors because only their declarations are read.</summary>
    internal static IReadOnlyList<IOutboxHandler> AllHandlers() =>
        [.. new[] { typeof(OutboxEmailSender).Assembly, typeof(AdminBroadcastOutboxHandler).Assembly }
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IOutboxHandler).IsAssignableFrom(t))
            .Select(t => (IOutboxHandler)RuntimeHelpers.GetUninitializedObject(t))];

    private async Task SeedOutboxAsync(Guid tenant)
    {
        await using var db = Fixture.CreateContext();
        db.Set<OutboxMessage>().AddRange(
            Row(OutboxEmailSender.MessageType, tenant),
            Row(WebhookOutboxHandler.MessageType, tenant),
            Row(BillingCancelOutboxHandler.MessageType, tenant));
        await db.SaveChangesAsync();
    }

    private static OutboxMessage Row(string type, Guid? tenant) => new()
    {
        Type = type,
        Payload = "{\"To\":\"member@x.com\",\"Subject\":\"s\"}",
        TenantId = tenant,
        CreatedAt = DateTimeOffset.UtcNow,
        NextAttemptAt = DateTimeOffset.UtcNow,
    };
}
