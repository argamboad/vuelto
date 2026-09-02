using Microsoft.EntityFrameworkCore;
using Perezosoft.Api.Services;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Core.Entities;

namespace Perezosoft.Api.Tests;

/// <summary>
/// The tenant invariants and invitation lifecycle, exercised against real Postgres so
/// the relational ExecuteUpdateAsync/savepoint paths actually run: single-owner,
/// always-in-exactly-one-tenant (re-home), transfer, dissolve gating, and the
/// invite → accept → revoke → regenerate flow.
/// </summary>
[Collection(PostgresCollection.Name)]
public class TenantInvariantTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task TransferOwnership_SwapsRoles()
    {
        var (oId, oTenant, mId) = await TwoMemberHouseholdAsync();

        await using (var db = Fixture.CreateContext(oTenant))
            Assert.Equal(TransferResult.Transferred,
                await new ServiceHarness(db).TenantService().TransferOwnershipAsync(oTenant, oId, mId));

        var roles = await RolesAsync(oTenant);
        Assert.Equal(TenantRoles.Member, roles[oId]);
        Assert.Equal(TenantRoles.Owner, roles[mId]);
    }

    [Fact]
    public async Task TransferOwnership_StaleOwner_GetsConcurrentModification_SingleOwnerHolds()
    {
        // v3 TB-ADM-13 (T45a): the conditional update (role flip guarded on the caller STILL being
        // owner) is what keeps the single-owner invariant under racing transfers. A caller whose
        // ownership already moved must get ConcurrentModification (the controller maps it to 409) —
        // and crucially must NOT mint a second owner.
        var (oId, oTenant, mId) = await TwoMemberHouseholdAsync();

        await using (var db = Fixture.CreateContext(oTenant))
            Assert.Equal(TransferResult.Transferred,
                await new ServiceHarness(db).TenantService().TransferOwnershipAsync(oTenant, oId, mId));

        // The ex-owner retries with a stale view of the world: 0 rows match the owner-guarded update.
        await using (var db = Fixture.CreateContext(oTenant))
            Assert.Equal(TransferResult.ConcurrentModification,
                await new ServiceHarness(db).TenantService().TransferOwnershipAsync(oTenant, oId, mId));

        var roles = await RolesAsync(oTenant);
        Assert.Equal(TenantRoles.Owner, roles[mId]);   // exactly one owner —
        Assert.Equal(TenantRoles.Member, roles[oId]);  // — and the loser stayed demoted
    }

    [Fact]
    public async Task RemoveMember_RemovesFromTenantAndReHomesAsOwner()
    {
        var (oId, oTenant, mId) = await TwoMemberHouseholdAsync();

        await using (var db = Fixture.CreateContext(oTenant))
            Assert.Equal(RemoveMemberResult.Removed,
                await new ServiceHarness(db).TenantService().RemoveMemberAsync(oTenant, mId));

        await using var read = Fixture.CreateContext();
        Assert.False(await read.TenantMemberships.AnyAsync(m => m.TenantId == oTenant && m.UserId == mId));
        var rehomed = await read.TenantMemberships.SingleAsync(m => m.UserId == mId);
        Assert.NotEqual(oTenant, rehomed.TenantId);
        Assert.Equal(TenantRoles.Owner, rehomed.Role);
    }

    [Fact]
    public async Task Leave_AsMember_LeavesAndReHomes()
    {
        var (_, oTenant, mId) = await TwoMemberHouseholdAsync();

        await using (var db = Fixture.CreateContext(oTenant))
            Assert.Equal(LeaveOutcome.Left,
                await new ServiceHarness(db).TenantService().LeaveAsync(mId, confirmDissolve: false));

        await using var read = Fixture.CreateContext();
        Assert.False(await read.TenantMemberships.AnyAsync(m => m.TenantId == oTenant && m.UserId == mId));
        Assert.True(await read.TenantMemberships.AnyAsync(m => m.UserId == mId)); // re-homed, never tenant-less
    }

    [Fact]
    public async Task Leave_OwnerWithMembers_MustTransferFirst()
    {
        var (oId, oTenant, _) = await TwoMemberHouseholdAsync();

        await using var db = Fixture.CreateContext(oTenant);
        Assert.Equal(LeaveOutcome.MustTransferFirst,
            await new ServiceHarness(db).TenantService().LeaveAsync(oId, confirmDissolve: false));
    }

    [Fact]
    public async Task Leave_SoleOwner_RequiresConfirmThenDissolves()
    {
        var (oId, oTenant) = await ProvisionAsync("solo@example.com");

        await using (var db = Fixture.CreateContext(oTenant))
            Assert.Equal(LeaveOutcome.ConfirmationRequired,
                await new ServiceHarness(db).TenantService().LeaveAsync(oId, confirmDissolve: false));

        await using (var db = Fixture.CreateContext(oTenant))
            Assert.Equal(LeaveOutcome.Dissolved,
                await new ServiceHarness(db).TenantService().LeaveAsync(oId, confirmDissolve: true));

        await using var read = Fixture.CreateContext();
        Assert.False(await read.Tenants.AnyAsync(t => t.Id == oTenant)); // wiped
        var rehomed = await read.TenantMemberships.SingleAsync(m => m.UserId == oId);
        Assert.NotEqual(oTenant, rehomed.TenantId);
    }

    [Fact]
    public async Task Invite_ExistingMember_ReturnsAlreadyMember()
    {
        var (oId, oTenant, _) = await TwoMemberHouseholdAsync();

        await using var db = Fixture.CreateContext(oTenant);
        var result = await new ServiceHarness(db).InvitationService().CreateAsync(oTenant, oId, "member@example.com");
        Assert.Equal(InviteCreateStatus.AlreadyMember, result.Status);
    }

    [Fact]
    public async Task Accept_InvalidToken_ReturnsInvalidToken()
    {
        var (uId, _) = await ProvisionAsync("u@example.com");

        await using var db = Fixture.CreateContext();
        Assert.Equal(AcceptStatus.InvalidToken,
            await new ServiceHarness(db).InvitationService().AcceptAsync(uId, "garbage-token"));
    }

    [Fact]
    public async Task Revoke_ThenAccept_IsRejected()
    {
        var (oId, oTenant) = await ProvisionAsync("owner@example.com");
        var (mId, _) = await ProvisionAsync("invitee@example.com");

        string token;
        await using (var db = Fixture.CreateContext(oTenant))
        {
            var svc = new ServiceHarness(db).InvitationService();
            var created = await svc.CreateAsync(oTenant, oId, "invitee@example.com");
            token = created.RawToken!;
            Assert.True(await svc.RevokeAsync(oTenant, created.Invitation!.Id));
        }

        await using (var db = Fixture.CreateContext())
            Assert.Equal(AcceptStatus.InvalidToken,
                await new ServiceHarness(db).InvitationService().AcceptAsync(mId, token));
    }

    [Fact]
    public async Task Regenerate_OldTokenRejected_NewTokenAccepts()
    {
        var (oId, oTenant) = await ProvisionAsync("owner2@example.com");
        var (mId, _) = await ProvisionAsync("join@example.com");

        string oldToken, newToken;
        await using (var db = Fixture.CreateContext(oTenant))
        {
            var svc = new ServiceHarness(db).InvitationService();
            var created = await svc.CreateAsync(oTenant, oId, "join@example.com");
            oldToken = created.RawToken!;
            var regen = await svc.RegenerateAsync(oTenant, created.Invitation!.Id, oId);
            newToken = regen.RawToken!;
        }

        await using (var db = Fixture.CreateContext())
            Assert.Equal(AcceptStatus.InvalidToken,
                await new ServiceHarness(db).InvitationService().AcceptAsync(mId, oldToken));

        await using (var db = Fixture.CreateContext())
            Assert.Equal(AcceptStatus.Joined,
                await new ServiceHarness(db).InvitationService().AcceptAsync(mId, newToken));

        await using var read = Fixture.CreateContext();
        Assert.Equal(oTenant, (await read.TenantMemberships.SingleAsync(m => m.UserId == mId)).TenantId);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<(Guid UserId, Guid TenantId)> ProvisionAsync(string email)
    {
        await using var db = Fixture.CreateContext();
        var user = await new ServiceHarness(db).UserService().GetOrCreateByEmailAsync(email);
        var membership = await db.TenantMemberships.SingleAsync(m => m.UserId == user.Id);
        return (user.Id, membership.TenantId);
    }

    /// <summary>Owner + one member sharing one tenant (member joined via the real accept flow).</summary>
    private async Task<(Guid OwnerId, Guid TenantId, Guid MemberId)> TwoMemberHouseholdAsync()
    {
        var (oId, oTenant) = await ProvisionAsync("owner@example.com");
        var (mId, _) = await ProvisionAsync("member@example.com");

        string token;
        await using (var db = Fixture.CreateContext(oTenant))
            token = (await new ServiceHarness(db).InvitationService()
                .CreateAsync(oTenant, oId, "member@example.com")).RawToken!;

        // Accept runs while the member's JWT still holds their old tenant — the
        // invitation lookups must bypass the global filter (proves Phase 1's exemption).
        await using (var db = Fixture.CreateContext())
            Assert.Equal(AcceptStatus.Joined,
                await new ServiceHarness(db).InvitationService().AcceptAsync(mId, token));

        return (oId, oTenant, mId);
    }

    private async Task<Dictionary<Guid, string>> RolesAsync(Guid tenantId)
    {
        await using var read = Fixture.CreateContext();
        return await read.TenantMemberships.Where(m => m.TenantId == tenantId)
            .ToDictionaryAsync(m => m.UserId, m => m.Role);
    }
}
