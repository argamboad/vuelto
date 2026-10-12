using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Billing;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Billing;
using Vuelto.Infrastructure.Repositories;
using Vuelto.Infrastructure.Webhooks;

namespace Vuelto.Api.Tests.Tenancy;

/// <summary>
/// v4 audit H5 (T21, LB-AUTH-5 ≡ LB-BILL-19, R123): a solo owner whose household holds no content can accept an
/// invitation elsewhere, and their old household is dissolved on the way. It used to be deleted with a bare
/// <c>Tenants.Remove</c>, so no <see cref="Vuelto.Core.Abstractions.ITenantDataContributor"/> ran: the Stripe
/// subscription kept charging (no <c>billing.cancel</c>), and the API keys, webhook subscriptions, usage counters
/// and billing projection stayed behind with no tenant — and an orphaned API key still authenticated, because
/// <c>AuthenticateAsync</c> finds a key by its hash across every tenant. Those rows are exactly the ones whose
/// contributors say "not content", so they never block the accept: dissolving is the only thing that removes them.
/// (An audit trail does count as content, so a household with one refuses the accept instead — not this path.)
/// Accept now dissolves through <see cref="ITenantDissolutionService"/>, the same sequence as a sole owner leaving
/// and as account erasure.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AcceptDissolveTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task Accept_DissolvingAnEmptyTenantOfOne_WipesPlumbingRows()
    {
        var host = Guid.CreateVersion7();
        await SeedTenantWithOwnerAsync(host);
        var token = await SeedPendingInviteAsync(host, "joiner@x.com");
        var (joinerId, oldTenant) = await ProvisionInviteeAsync("joiner@x.com");
        var rawKey = await SeedPlumbingAsync(oldTenant, joinerId);

        var ambient = new TestCurrentTenant();
        await using (var db = Fixture.CreateTestContext(ambient))
        {
            var harness = new ServiceHarness(db, currentTenant: ambient);
            Assert.Equal(AcceptStatus.Joined,
                await harness.InvitationService(contributors: harness.Contributors()).AcceptAsync(joinerId, token));
        }

        await using var read = Fixture.CreateContext();
        Assert.Equal(host, (await read.TenantMemberships.SingleAsync(m => m.UserId == joinerId)).TenantId);
        Assert.False(await read.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == oldTenant));
        Assert.Equal(0, await read.Set<ApiKey>().IgnoreQueryFilters().CountAsync(k => k.TenantId == oldTenant));
        Assert.Equal(0, await read.Set<WebhookSubscription>().IgnoreQueryFilters().CountAsync(w => w.TenantId == oldTenant));
        Assert.Equal(0, await read.Set<UsageCounter>().IgnoreQueryFilters().CountAsync(u => u.TenantId == oldTenant));
        Assert.Equal(0, await read.Set<Subscription>().IgnoreQueryFilters().CountAsync(s => s.TenantId == oldTenant));

        // Stripe stops charging: the provider cancel is queued for the dissolved tenant's subscription.
        var cancel = Assert.Single(await read.Set<OutboxMessage>().Where(m => m.Type == BillingCancelOutboxHandler.MessageType).ToListAsync());
        Assert.Equal(oldTenant, cancel.TenantId);
        Assert.Contains("sub_live_joiner", cancel.Payload, StringComparison.Ordinal);

        // And the old household's API key no longer opens anything.
        Assert.Null(await new ApiKeyService(new EfRepository<ApiKey>(read), new TokenGenerator(), new TokenHasher(),
            new TestCurrentTenant(), TimeProvider.System).AuthenticateAsync(rawKey));
    }

    [Fact]
    public void DeleteTenantAsync_IsCalledOnlyByTheDissolutionSequence()
    {
        // A household leaves the database one way: ITenantDissolutionService — every contributor, then the core
        // teardown. A second, raw way to delete a tenant is how the accept path skipped the contributors, so
        // none may exist: no DeleteTenantAsync, and no Tenants.Remove outside the repository's core teardown.
        var sep = Path.DirectorySeparatorChar;
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{sep}bin{sep}") || file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}Migrations{sep}"))
                continue;
            var source = File.ReadAllText(file);
            if (source.Contains("DeleteTenantAsync", StringComparison.Ordinal))
                offenders.Add($"{Path.GetFileName(file)}: DeleteTenantAsync");
            if (Regex.IsMatch(source, @"\bTenants\s*\.\s*Remove(Range)?\s*\("))
                offenders.Add($"{Path.GetFileName(file)}: Tenants.Remove");
        }

        Assert.True(offenders.Count == 0,
            "A tenant is deleted only through ITenantDissolutionService (contributors first, then WipeDataAsync) — "
            + "a raw delete skips billing.cancel and orphans keys, webhooks and metering (v4 H5, R123): "
            + string.Join(", ", offenders));
    }

    // --- seeding (shapes mirror AcceptSeatQuotaTests) ---

    private async Task SeedTenantWithOwnerAsync(Guid tenant)
    {
        await using var db = Fixture.CreateContext(tenant);
        var owner = Guid.CreateVersion7();
        db.Set<Tenant>().Add(new Tenant { Id = tenant, Name = "Host", CreatedAt = DateTimeOffset.UtcNow });
        db.Set<User>().Add(new User { Id = owner, Email = $"owner-{owner:N}@x.com" });
        db.Set<TenantMembership>().Add(new TenantMembership { TenantId = tenant, UserId = owner, Role = TenantRoles.Owner });
        await db.SaveChangesAsync();
    }

    /// <summary>Seeds a redeemable pending invitation and returns its RAW token.</summary>
    private async Task<string> SeedPendingInviteAsync(Guid tenant, string email)
    {
        var raw = $"raw-{Guid.NewGuid():N}";
        await using var db = Fixture.CreateContext(tenant);
        var inviter = Guid.CreateVersion7();
        db.Set<User>().Add(new User { Id = inviter, Email = $"inv-{inviter:N}@x.com" });
        db.Set<TenantInvitation>().Add(new TenantInvitation
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant,
            InvitedEmail = email,
            InvitedByUserId = inviter,
            Status = InvitationStatuses.Pending,
            TokenHash = new TokenHasher().HashToken(raw),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        });
        await db.SaveChangesAsync();
        return raw;
    }

    /// <summary>Provisions the invitee as a fresh user with an empty tenant-of-one; returns (userId, their tenant).</summary>
    private async Task<(Guid UserId, Guid TenantId)> ProvisionInviteeAsync(string email)
    {
        await using var db = Fixture.CreateContext();
        var user = await new ServiceHarness(db).UserService().GetOrCreateByEmailAsync(email);
        var membership = await db.TenantMemberships.AsNoTracking().SingleAsync(m => m.UserId == user.Id);
        return (user.Id, membership.TenantId);
    }

    /// <summary>Everything a household accrues that isn't content: an API key (returns its raw value), a webhook
    /// subscription, a usage counter and a Stripe-backed subscription. None of them writes an audit event.</summary>
    private async Task<string> SeedPlumbingAsync(Guid tenant, Guid owner)
    {
        await using var db = Fixture.CreateContext(tenant);
        var key = await new ApiKeyService(new EfRepository<ApiKey>(db), new TokenGenerator(), new TokenHasher(),
            new TestCurrentTenant(), TimeProvider.System).CreateAsync(owner, "ci", null, null, default);
        db.Set<WebhookSubscription>().Add(new WebhookSubscription
        {
            Url = "https://recv.test/h",
            EventTypes = "ping",
            EncryptedSecret = new WebhookSecretProtector(new EphemeralDataProtectionProvider()).Protect("whsec_x"),
            CreatedByUserId = owner,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Set<UsageCounter>().Add(new UsageCounter { Key = "export", Period = "2026-09", Count = 1 });
        db.Set<Subscription>().Add(new Subscription
        {
            PlanKey = PlanKeys.Pro,
            Status = SubscriptionStatus.Active,
            StripeSubscriptionId = "sub_live_joiner",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return key!.RawKey;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root.");
    }
}
