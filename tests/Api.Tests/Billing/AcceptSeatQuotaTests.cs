using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Billing;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests.Billing;

/// <summary>
/// BILLING-9 (ADR-006 addendum): the seat quota is re-checked when an invitation is ACCEPTED, not
/// just when it is created — a downgrade (dunning lapse, cancel, admin comp revert) leaves pending
/// invites that reserve more seats than the new plan allows, and redeeming them must not grow the
/// tenant further past its cap. The accept itself is seat-neutral (the joiner consumes the seat
/// their pending invite reserved), so accepts at exactly the cap stay allowed; only an over-cap
/// tenant refuses. Free plan seat limit pinned by the catalog: 3.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AcceptSeatQuotaTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task Accept_OverDowngradedCap_ReturnsSeatLimitReached_AndMovesNothing()
    {
        // 3 members + 1 pending = 4 reserved seats on Free(3) — the post-downgrade state.
        var tenant = Guid.CreateVersion7();
        await SeedTenantWithMembersAsync(tenant, members: 3);
        var token = await SeedPendingInviteAsync(tenant, "late-joiner@x.com");
        var (inviteeId, inviteeTenant) = await ProvisionInviteeAsync("late-joiner@x.com");

        // Shared ambient tenant: the invitee calls with no current tenant; AcceptAsync's
        // EnterTenant must drive the context's filter for the quota to count the right tenant.
        var ambient = new TestCurrentTenant();
        await using var db = Fixture.CreateTestContext(ambient);
        var svc = new ServiceHarness(db, currentTenant: ambient).InvitationService();

        Assert.Equal(AcceptStatus.SeatLimitReached, await svc.AcceptAsync(inviteeId, token));

        await using var read = Fixture.CreateContext();
        var membership = await read.TenantMemberships.SingleAsync(m => m.UserId == inviteeId);
        Assert.Equal(inviteeTenant, membership.TenantId); // not moved
        Assert.Equal(InvitationStatuses.Pending,          // token not consumed — self-heals on upgrade
            (await read.TenantInvitations.IgnoreQueryFilters().SingleAsync(i => i.TenantId == tenant)).Status);
    }

    [Fact]
    public async Task Accept_AtCap_IsSeatNeutral_AndJoins()
    {
        // 2 members + 1 pending = exactly 3/3 — the invite reserved the last seat; redeeming it
        // swaps the reservation for a membership and must succeed.
        var tenant = Guid.CreateVersion7();
        await SeedTenantWithMembersAsync(tenant, members: 2);
        var token = await SeedPendingInviteAsync(tenant, "third@x.com");
        var (inviteeId, _) = await ProvisionInviteeAsync("third@x.com");

        var ambient = new TestCurrentTenant();
        await using var db = Fixture.CreateTestContext(ambient);
        var svc = new ServiceHarness(db, currentTenant: ambient).InvitationService();

        Assert.Equal(AcceptStatus.Joined, await svc.AcceptAsync(inviteeId, token));

        await using var read = Fixture.CreateContext();
        Assert.Equal(3, await read.TenantMemberships.CountAsync(m => m.TenantId == tenant));
    }

    [Fact]
    public async Task Accept_SameInvitationConcurrently_JoinsExactlyOne()
    {
        // v3 TB-BILL backfill (T45b, the BILLING-9 race): the invitation reserved ONE seat, so two
        // users racing to redeem the SAME token must produce exactly one join — the conditional
        // status flip (TryAcceptAsync) is the guard; the loser's membership move rolls back with the
        // scope and they stay in their old tenant.
        var tenant = Guid.CreateVersion7();
        await SeedTenantWithMembersAsync(tenant, members: 2);
        var token = await SeedPendingInviteAsync(tenant, "contested@x.com"); // 2 + 1 pending = 3/3
        var (aId, aTenant) = await ProvisionInviteeAsync("racer-a@x.com");
        var (bId, bTenant) = await ProvisionInviteeAsync("racer-b@x.com");

        async Task<AcceptStatus> AcceptAsync(Guid userId)
        {
            var ambient = new TestCurrentTenant();
            await using var db = Fixture.CreateTestContext(ambient);
            return await new ServiceHarness(db, currentTenant: ambient).InvitationService().AcceptAsync(userId, token);
        }

        var results = await Task.WhenAll(AcceptAsync(aId), AcceptAsync(bId));

        // One winner, one InvalidToken (the flip already consumed it) — in either order.
        Assert.Single(results, r => r == AcceptStatus.Joined);
        Assert.Single(results, r => r == AcceptStatus.InvalidToken);

        await using var read = Fixture.CreateContext();
        Assert.Equal(3, await read.TenantMemberships.CountAsync(m => m.TenantId == tenant)); // 3/3, not 4
        var winnerIsA = results[0] == AcceptStatus.Joined;
        var (winner, loser, loserHome) = winnerIsA ? (aId, bId, bTenant) : (bId, aId, aTenant);
        Assert.Equal(tenant, (await read.TenantMemberships.SingleAsync(m => m.UserId == winner)).TenantId);
        Assert.Equal(loserHome, (await read.TenantMemberships.SingleAsync(m => m.UserId == loser)).TenantId); // rolled back home
    }

    [Fact]
    public async Task Accept_BlockedOverCap_SelfHealsWhenTheTenantUpgrades()
    {
        var tenant = Guid.CreateVersion7();
        await SeedTenantWithMembersAsync(tenant, members: 3);
        var token = await SeedPendingInviteAsync(tenant, "healed@x.com");
        var (inviteeId, _) = await ProvisionInviteeAsync("healed@x.com");

        var ambient = new TestCurrentTenant();
        await using (var db = Fixture.CreateTestContext(ambient))
        {
            var svc = new ServiceHarness(db, currentTenant: ambient).InvitationService();
            Assert.Equal(AcceptStatus.SeatLimitReached, await svc.AcceptAsync(inviteeId, token));
        }

        // Back on Pro (10 seats) — the SAME still-pending token now redeems.
        await using (var seed = Fixture.CreateContext(tenant))
        {
            seed.Set<Subscription>().Add(new Subscription
            {
                PlanKey = PlanKeys.Pro,
                Status = SubscriptionStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var ambient2 = new TestCurrentTenant();
        await using (var db = Fixture.CreateTestContext(ambient2))
        {
            var svc = new ServiceHarness(db, currentTenant: ambient2).InvitationService();
            Assert.Equal(AcceptStatus.Joined, await svc.AcceptAsync(inviteeId, token));
        }
    }

    // --- helpers (seed shapes mirror QuotaServiceTests) ---

    private async Task SeedTenantWithMembersAsync(Guid tenant, int members)
    {
        await using var db = Fixture.CreateContext(tenant);
        db.Set<Tenant>().Add(new Tenant { Id = tenant, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
        for (var i = 0; i < members; i++)
        {
            var uid = Guid.CreateVersion7();
            db.Set<User>().Add(new User { Id = uid, Email = $"m{i}-{uid:N}@x.com" });
            db.Set<TenantMembership>().Add(new TenantMembership
            {
                TenantId = tenant,
                UserId = uid,
                Role = i == 0 ? TenantRoles.Owner : TenantRoles.Member,
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Seeds a redeemable pending invitation and returns its RAW token.</summary>
    private async Task<string> SeedPendingInviteAsync(Guid tenant, string email)
    {
        var raw = $"e2e-raw-{Guid.NewGuid():N}";
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
}
