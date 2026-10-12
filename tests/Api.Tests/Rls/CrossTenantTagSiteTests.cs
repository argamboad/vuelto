using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Core.Webhooks;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;
using Vuelto.Infrastructure.Webhooks;

namespace Vuelto.Api.Tests.Rls;

/// <summary>
/// v4 audit T25 (R151): every <c>TagWith(RlsTags.CrossTenant)</c> site in <c>src/</c> is a sanctioned hole in
/// the RLS backstop, and each is paired here with a test that names it. Each test runs the real code as the
/// runtime role with some OTHER tenant ambient: the read reaches the foreign row, which only the tag allows — so
/// a site that lost its tag would fail closed (find nothing) and its test with it. The gate at the bottom holds
/// the list to the tree: a new tag site fails until it is listed with its test.
/// (<c>GetValidByEmailAcrossTenantsAsync</c>'s pairing lives with the signup gate it serves, in SignupGateTests.)
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CrossTenantTagSiteTests(PostgresFixture fixture) : IAsyncLifetime
{
    private Guid _mine;
    private Guid _other;
    private Guid _invitationId;
    private const string TokenHash = "tag-site-token-hash";

    public async Task InitializeAsync()
    {
        await fixture.ResetAsync();
        await using (var db = fixture.CreateTestContext())
            await RlsTestSetup.ProvisionAsync(db);

        var pair = await TwoTenants.SeedAsync(async tenant =>
        {
            await using var db = fixture.CreateContext();
            db.Tenants.Add(new Tenant { Id = tenant, Name = "Household", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });
        (_mine, _other) = (pair.Mine, pair.Other);

        // The foreign row every test must reach: an invitation in the OTHER tenant.
        await using var seed = fixture.CreateContext(_other);
        var invitation = new TenantInvitation
        {
            TenantId = _other, InvitedEmail = "guest@example.com", InvitedByUserId = Guid.CreateVersion7(),
            Status = InvitationStatuses.Pending, TokenHash = TokenHash,
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        };
        seed.TenantInvitations.Add(invitation);
        await seed.SaveChangesAsync();
        _invitationId = invitation.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>A context connected as the runtime role (subject to RLS) with <paramref name="tenantId"/> ambient.</summary>
    private TestAppDbContext RuntimeContext(Guid? tenantId)
    {
        var options = new DbContextOptionsBuilder<TestAppDbContext>()
            .UseNpgsql(RlsTestSetup.RuntimeConnectionString(fixture.ConnectionString)).Options;
        return new TestAppDbContext(options, new TestCurrentTenant { TenantId = tenantId });
    }

    [Fact]
    public async Task Probe_WithoutTheTag_TheForeignInvitationIsInvisible()
    {
        // What every test below relies on: with another tenant ambient, RLS hides the row from an untagged read.
        await using var db = RuntimeContext(_mine);
        Assert.Empty(await db.TenantInvitations.IgnoreQueryFilters().Where(i => i.Id == _invitationId).ToListAsync());
    }

    [Fact]
    public async Task QueryAllTenants_ReachesAnotherTenantsRow_OnlyBecauseOfItsTag()
    {
        await using var db = RuntimeContext(_mine);
        var found = await new EfRepository<TenantInvitation>(db).QueryAllTenants().Where(i => i.Id == _invitationId).ToListAsync();
        Assert.Equal(_other, Assert.Single(found).TenantId);
    }

    [Fact]
    public async Task GetByIdUnscopedAsync_ReachesAnotherTenantsInvitation_OnlyBecauseOfItsTag()
    {
        await using var db = RuntimeContext(_mine);
        var found = await new TenantInvitationRepository(db).GetByIdUnscopedAsync(_invitationId);
        Assert.Equal(_other, found?.TenantId);
    }

    [Fact]
    public async Task GetByTokenHashAsync_ReachesAnotherTenantsInvitation_OnlyBecauseOfItsTag()
    {
        await using var db = RuntimeContext(_mine);
        var found = await new TenantInvitationRepository(db).GetByTokenHashAsync(TokenHash);
        Assert.Equal(_other, found?.TenantId);
    }

    [Fact]
    public async Task WebhookHandleAsync_LoadsAnotherTenantsSubscription_OnlyBecauseOfItsTag()
    {
        // The dispatcher is tenant-less in production (the system context already bypasses); the tag is what keeps
        // the delivery working if a handler ever runs with a tenant ambient.
        var protector = new WebhookSecretProtector(new EphemeralDataProtectionProvider());
        var subscription = new WebhookSubscription
        {
            TenantId = _other, Url = "https://recv.test/h", EventTypes = WebhookEvents.Ping,
            EncryptedSecret = protector.Protect("whsec_test"), CreatedByUserId = Guid.CreateVersion7(), CreatedAt = DateTimeOffset.UtcNow,
        };
        await using (var seed = fixture.CreateContext(_other))
        {
            seed.Set<WebhookSubscription>().Add(subscription);
            await seed.SaveChangesAsync();
        }

        var receiver = new CountingReceiver();
        var message = new OutboxMessage
        {
            Type = WebhookOutboxHandler.MessageType,
            Payload = JsonSerializer.Serialize(new WebhookOutboxPayload(subscription.Id, WebhookEvents.Ping, "e1", "{}")),
            CreatedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow,
        };

        await using var db = RuntimeContext(_mine);
        await new WebhookOutboxHandler(db, new WebhookSender(new HttpClient(receiver), new AllowAllUrlGuard()), protector,
            TimeProvider.System, fixture.CreateContextFactory()).HandleAsync(message);

        Assert.Equal(1, receiver.Requests); // the subscription was found, so the POST went out
    }

    [Fact]
    public void EveryCrossTenantTagSite_IsPairedWithATestNamingIt()
    {
        var expected = new Dictionary<string, string>
        {
            ["src/Infrastructure/Repositories/EfRepository.cs:QueryAllTenants"] = nameof(QueryAllTenants_ReachesAnotherTenantsRow_OnlyBecauseOfItsTag),
            ["src/Infrastructure/Repositories/TenantInvitationRepository.cs:GetByIdUnscopedAsync"] = nameof(GetByIdUnscopedAsync_ReachesAnotherTenantsInvitation_OnlyBecauseOfItsTag),
            ["src/Infrastructure/Repositories/TenantInvitationRepository.cs:GetValidByEmailAcrossTenantsAsync"] = "GetValidByEmailAcrossTenantsAsync_SeesOtherTenantsInvitations_OnlyBecauseOfItsTag",
            ["src/Infrastructure/Repositories/TenantInvitationRepository.cs:GetByTokenHashAsync"] = nameof(GetByTokenHashAsync_ReachesAnotherTenantsInvitation_OnlyBecauseOfItsTag),
            ["src/Infrastructure/Webhooks/WebhookOutboxHandler.cs:HandleAsync"] = nameof(WebhookHandleAsync_LoadsAnotherTenantsSubscription_OnlyBecauseOfItsTag),
        };

        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !root.EnumerateFiles("*.slnx").Any()) root = root.Parent;
        static bool Built(string f) => f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}");

        var sites = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!.FullName, "src"), "*.cs", SearchOption.AllDirectories).Where(f => !Built(f)))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains(".TagWith(RlsTags.CrossTenant)")) continue;
                // The enclosing member: the nearest public declaration above the site.
                var member = Enumerable.Range(0, i + 1).Reverse()
                    .Select(n => Regex.Match(lines[n], @"^\s{4}public [^=(]*?\b(\w+)(?:<[^>]+>)?\("))
                    .First(m => m.Success).Groups[1].Value;
                sites.Add($"{Path.GetRelativePath(root.FullName, file).Replace('\\', '/')}:{member}");
            }
        }

        Assert.Equal(expected.Keys.Order(), sites.Order()); // a new tag site: list it here with the test that names it
        var tests = string.Join('\n', Directory.EnumerateFiles(Path.Combine(root.FullName, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !Built(f)).Select(File.ReadAllText));
        Assert.All(expected.Values, name => Assert.Matches($@"\bTask\s+{name}\(", tests));
    }

    private sealed class CountingReceiver : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
