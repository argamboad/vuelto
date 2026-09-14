using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Configuration;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Tests.Auth;

/// <summary>
/// GATES-2 (ADR-027): the signup green list. The rule it enforces, in one line — <b>the green list
/// decides who may FOUND a household</b>; inside a household owned by a green-listed person, membership
/// is that owner's business, bounded only by the seat cap.
/// <para>
/// So the invitation bypass keys on the household's <b>owner</b>, not on whoever clicked invite. That is
/// what lets a non-listed admin member invite into their owner's household, while a household whose
/// owner is not listed admits nobody new to the app. Someone who leaves gets re-homed into a
/// tenant-of-one they own (<c>TenantService.ReHomeAsync</c>) — harmless, because that tenant's owner is
/// not green-listed either, so it can pull nobody in.
/// </para>
/// The gate runs at the single account-creation choke point, so these tests exercise it through
/// <see cref="UserService"/> — the one place every sign-in path funnels through.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SignupGateTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private static SignupSettings ListOf(params string[] emails) => new() { AllowedEmails = emails };

    [Fact]
    public async Task NoGreenList_AnyoneMayFoundAHousehold()
    {
        // The shipped default. A template whose fresh apps are born locked would be the wrong default.
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db, signup: new SignupSettings()).UserService();

        var user = await sut.GetOrCreateByEmailAsync("anyone@example.com");

        Assert.Equal("anyone@example.com", user.Email);
    }

    [Fact]
    public async Task GreenListedAddress_MayFoundAHousehold()
    {
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db, signup: ListOf("Friend@Example.com")).UserService();

        var user = await sut.GetOrCreateByEmailAsync("friend@example.com"); // matched case-insensitively

        await using var read = Fixture.CreateContext();
        Assert.Equal(TenantRoles.Owner, (await read.TenantMemberships.SingleAsync(m => m.UserId == user.Id)).Role);
    }

    [Fact]
    public async Task GreenListedDomain_MayFoundAHousehold()
    {
        await using var db = Fixture.CreateContext();
        var settings = new SignupSettings { AllowedDomains = ["Example.com"] };
        var sut = new ServiceHarness(db, signup: settings).UserService();

        var user = await sut.GetOrCreateByEmailAsync("someone@example.com");

        Assert.NotEqual(Guid.Empty, user.Id);
    }

    [Fact]
    public async Task Stranger_IsRefused_AndNothingIsCreated()
    {
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db, signup: ListOf("friend@example.com")).UserService();

        await Assert.ThrowsAsync<SignupNotAllowedException>(() =>
            sut.GetOrCreateByEmailAsync("stranger@example.com"));

        await using var read = Fixture.CreateContext();
        Assert.False(await read.Users.AnyAsync(u => u.Email == "stranger@example.com"));
        Assert.Empty(await read.Tenants.ToListAsync()); // no orphan household left behind either
    }

    [Fact]
    public async Task ExistingAccount_SignsIn_EvenWhenNotGreenListed()
    {
        // The gate is on CREATION only. Gating sign-in would lock out everyone who already has data the
        // moment the list is edited — the failure mode that makes a green list unusable in practice.
        await using var db = Fixture.CreateContext();
        var open = new ServiceHarness(db, signup: new SignupSettings()).UserService();
        var existing = await open.GetOrCreateByEmailAsync("early@example.com");

        await using var db2 = Fixture.CreateContext();
        var restricted = new ServiceHarness(db2, signup: ListOf("someone-else@example.com")).UserService();
        var again = await restricted.GetOrCreateByEmailAsync("early@example.com");

        Assert.Equal(existing.Id, again.Id);
    }

    [Fact]
    public async Task OAuthPath_IsGatedToo()
    {
        // Every user-minting path funnels through the same choke point — the email paths are not a
        // side door with its own rules, and neither is the provider button next to them.
        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db, signup: ListOf("friend@example.com")).UserService();

        await Assert.ThrowsAsync<SignupNotAllowedException>(() =>
            sut.GetOrCreateUserAsync("stranger@example.com", "g-1", "google", emailVerified: true));

        await using var read = Fixture.CreateContext();
        Assert.False(await read.UserLogins.AnyAsync(l => l.ProviderUserId == "g-1"));
    }

    // ── The invitation bypass ────────────────────────────────────────────────

    [Fact]
    public async Task InvitedInto_AGreenListedOwnersHousehold_MaySignUp()
    {
        var (tenant, _) = await SeedHouseholdAsync(ownerEmail: "friend@example.com");
        await SeedPendingInviteAsync(tenant, "friends-wife@example.com");

        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db, signup: ListOf("friend@example.com")).UserService();

        var user = await sut.GetOrCreateByEmailAsync("friends-wife@example.com");

        Assert.Equal("friends-wife@example.com", user.Email);
    }

    [Fact]
    public async Task InvitedBy_AnAdminMember_StillWorks_BecauseTheOwnerIsWhatCounts()
    {
        // The owner delegated: an admin member (not on the list) sent the invitation. It still admits,
        // because the household belongs to someone green-listed.
        var (tenant, _) = await SeedHouseholdAsync(ownerEmail: "friend@example.com");
        var adminId = await SeedMemberAsync(tenant, "admin@example.com", TenantRoles.Admin);
        await SeedPendingInviteAsync(tenant, "newcomer@example.com", invitedBy: adminId);

        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db, signup: ListOf("friend@example.com")).UserService();

        var user = await sut.GetOrCreateByEmailAsync("newcomer@example.com");

        Assert.NotEqual(Guid.Empty, user.Id);
    }

    [Fact]
    public async Task InvitationFrom_AHouseholdWhoseOwnerIsNotListed_AdmitsNobody()
    {
        // The one-hop boundary. Without it the list leaks outward: whoever gets in invites the next
        // person, who invites the next.
        var (tenant, _) = await SeedHouseholdAsync(ownerEmail: "not-listed@example.com");
        await SeedPendingInviteAsync(tenant, "stranger@example.com");

        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db, signup: ListOf("friend@example.com")).UserService();

        await Assert.ThrowsAsync<SignupNotAllowedException>(() =>
            sut.GetOrCreateByEmailAsync("stranger@example.com"));
    }

    [Fact]
    public async Task ExpiredInvitation_AdmitsNobody()
    {
        var (tenant, _) = await SeedHouseholdAsync(ownerEmail: "friend@example.com");
        await SeedPendingInviteAsync(tenant, "late@example.com", expiresAt: DateTimeOffset.UtcNow.AddDays(-1));

        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db, signup: ListOf("friend@example.com")).UserService();

        await Assert.ThrowsAsync<SignupNotAllowedException>(() =>
            sut.GetOrCreateByEmailAsync("late@example.com"));
    }

    [Fact]
    public async Task AlreadyAcceptedInvitation_AdmitsNobody()
    {
        // Single-use in the admission sense too: a redeemed invitation is not a standing pass for the
        // address it named.
        var (tenant, _) = await SeedHouseholdAsync(ownerEmail: "friend@example.com");
        await SeedPendingInviteAsync(tenant, "used@example.com", status: InvitationStatuses.Accepted);

        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db, signup: ListOf("friend@example.com")).UserService();

        await Assert.ThrowsAsync<SignupNotAllowedException>(() =>
            sut.GetOrCreateByEmailAsync("used@example.com"));
    }

    [Fact]
    public async Task RevokedInvitation_AdmitsNobody()
    {
        var (tenant, _) = await SeedHouseholdAsync(ownerEmail: "friend@example.com");
        await SeedPendingInviteAsync(tenant, "revoked@example.com", status: InvitationStatuses.Revoked);

        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db, signup: ListOf("friend@example.com")).UserService();

        await Assert.ThrowsAsync<SignupNotAllowedException>(() =>
            sut.GetOrCreateByEmailAsync("revoked@example.com"));
    }

    [Fact]
    public async Task AnInvitationToSomeoneElse_DoesNotAdmitYou()
    {
        var (tenant, _) = await SeedHouseholdAsync(ownerEmail: "friend@example.com");
        await SeedPendingInviteAsync(tenant, "the-invitee@example.com");

        await using var db = Fixture.CreateContext();
        var sut = new ServiceHarness(db, signup: ListOf("friend@example.com")).UserService();

        await Assert.ThrowsAsync<SignupNotAllowedException>(() =>
            sut.GetOrCreateByEmailAsync("someone-else@example.com"));
    }

    // ── Seeding ──────────────────────────────────────────────────────────────

    /// <summary>A tenant whose owner is <paramref name="ownerEmail"/>. Returns (tenantId, ownerUserId).</summary>
    private async Task<(Guid TenantId, Guid OwnerId)> SeedHouseholdAsync(string ownerEmail)
    {
        await using var db = Fixture.CreateContext();
        var tenantId = Guid.CreateVersion7();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Household", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        var ownerId = await AddUserAsync(db, tenantId, ownerEmail, TenantRoles.Owner);
        await db.SaveChangesAsync();
        return (tenantId, ownerId);
    }

    private async Task<Guid> SeedMemberAsync(Guid tenantId, string email, string role)
    {
        await using var db = Fixture.CreateContext();
        var id = await AddUserAsync(db, tenantId, email, role);
        await db.SaveChangesAsync();
        return id;
    }

    private static Task<Guid> AddUserAsync(Vuelto.Infrastructure.Persistence.AppDbContext db, Guid tenantId, string email, string role)
    {
        var userId = Guid.CreateVersion7();
        db.Users.Add(new User { Id = userId, Email = email, CreatedAt = DateTimeOffset.UtcNow });
        db.TenantMemberships.Add(new TenantMembership
        {
            Id = Guid.CreateVersion7(), TenantId = tenantId, UserId = userId, Role = role, JoinedAt = DateTimeOffset.UtcNow,
        });
        return Task.FromResult(userId);
    }

    private async Task SeedPendingInviteAsync(Guid tenantId, string invitedEmail, Guid? invitedBy = null,
        DateTimeOffset? expiresAt = null, string status = InvitationStatuses.Pending)
    {
        await using var db = Fixture.CreateContext(tenantId);
        db.TenantInvitations.Add(new TenantInvitation
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            InvitedEmail = invitedEmail,
            InvitedByUserId = invitedBy ?? Guid.CreateVersion7(),
            Status = status,
            TokenHash = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddDays(7),
        });
        await db.SaveChangesAsync();
    }
}
