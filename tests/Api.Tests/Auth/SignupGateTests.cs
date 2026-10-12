using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Configuration;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;
using Vuelto.Infrastructure.Repositories;

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
    public async Task Invitation_ExpiringNow_IsInvalidForGateAndAccept()
    {
        // v4 LB-AUTH-6 (T34, R127): three rules disagreed at the expiry instant — the gate's query said valid
        // (>= now), the accept said invalid (<= now) — so at that second the green-list gate admitted the
        // invitee and let them found a household, and the accept refused them one call later. One predicate
        // now, and at the instant it says no to both.
        var (tenant, _) = await SeedHouseholdAsync(ownerEmail: "friend@example.com");
        var expiry = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        var raw = $"raw-{Guid.NewGuid():N}";
        await SeedPendingInviteAsync(tenant, "late@example.com", expiresAt: expiry, tokenHash: new TokenHasher().HashToken(raw));
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(expiry.AddSeconds(-1));

        await using var db = Fixture.CreateContext();
        var harness = new ServiceHarness(db, clock, signup: ListOf("friend@example.com"));

        Assert.True(await harness.SignupGate().IsAllowedAsync("late@example.com")); // a second before: yes

        clock.Advance(TimeSpan.FromSeconds(1)); // the expiry instant: no, from both
        Assert.False(await harness.SignupGate().IsAllowedAsync("late@example.com"));
        Assert.Equal(AcceptStatus.InvalidToken, await harness.InvitationService().AcceptAsync(Guid.CreateVersion7(), raw));
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

    // ── Isolation: what the gate must NOT do (v4 audit T25, R151) ────────────
    // The tests above check outcomes. These pin the properties a refactor could break without changing one:
    // the gate admits, it never joins; it reads only the households that invited the address; it is not an
    // account-existence oracle; and its answer is a property of the invitations, not of their order.

    [Fact]
    public async Task InviteeAdmittedByTheGate_FoundsTheirOwnHousehold_NotTheInviters()
    {
        // The invitation lets the account be CREATED. Joining the inviting household is the accept, with its
        // token — an owner cannot pull an address into their household by inviting it and waiting for a sign-in.
        var (inviting, _) = await SeedHouseholdAsync(ownerEmail: "friend@example.com");
        await SeedPendingInviteAsync(inviting, "guest@example.com");

        await using var db = Fixture.CreateContext();
        var user = await new ServiceHarness(db, signup: ListOf("friend@example.com")).UserService()
            .GetOrCreateByEmailAsync("guest@example.com");

        await using var read = Fixture.CreateContext();
        var membership = await read.TenantMemberships.SingleAsync(m => m.UserId == user.Id);
        Assert.NotEqual(inviting, membership.TenantId);
        Assert.Equal(TenantRoles.Owner, membership.Role);
        await using var inTheInvitingHousehold = Fixture.CreateContext(inviting);
        Assert.Equal(InvitationStatuses.Pending,
            (await inTheInvitingHousehold.TenantInvitations.SingleAsync(i => i.InvitedEmail == "guest@example.com")).Status); // untouched
    }

    [Fact]
    public async Task Gate_ReadsOnlyTheHouseholdsThatInvitedTheAddress()
    {
        // A pre-auth, cross-tenant read: prove it narrow with the record, not with the answer. A hostile owner
        // inviting an address must not make the gate read any household but the ones holding that invitation.
        var (inviting, _) = await SeedHouseholdAsync(ownerEmail: "friend@example.com");
        var (unlisted, _) = await SeedHouseholdAsync(ownerEmail: "unlisted@example.com");
        var (bystander, _) = await SeedHouseholdAsync(ownerEmail: "bystander@example.com");
        await SeedPendingInviteAsync(unlisted, "guest@example.com");
        await SeedPendingInviteAsync(inviting, "guest@example.com");
        await SeedPendingInviteAsync(bystander, "someone-else@example.com");

        await using var db = Fixture.CreateContext();
        var harness = new ServiceHarness(db, signup: ListOf("friend@example.com"));
        Assert.True(await harness.SignupGate().IsAllowedAsync("guest@example.com"));

        Assert.NotEmpty(harness.TenantCalls);
        Assert.All(harness.TenantCalls, c => Assert.Equal(nameof(ITenantRepository.GetMemberDetailsAsync), c.Method));
        var read = harness.TenantCalls.Select(c => (Guid)c.Args[0]!).ToHashSet();
        Assert.Subset(new HashSet<Guid> { inviting, unlisted }, read);
        Assert.DoesNotContain(bystander, read);
    }

    [Fact]
    public async Task Gate_IsNeverConsulted_ForAnExistingAccount_AndOnceForANewOne()
    {
        // Were the gate asked about existing accounts, its refusal would tell a stranger which addresses have
        // one. The choke point checks for the account FIRST; the record shows the gate was not called.
        await using (var seed = Fixture.CreateContext())
            await new ServiceHarness(seed).UserService().GetOrCreateByEmailAsync("early@example.com");

        await using var db = Fixture.CreateContext();
        var harness = new ServiceHarness(db, signup: ListOf("friend@example.com"));
        var users = harness.UserService();

        await users.GetOrCreateByEmailAsync("early@example.com");
        Assert.Empty(harness.SignupGateCalls);

        await users.GetOrCreateByEmailAsync("friend@example.com");
        Assert.Single(harness.SignupGateCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Gate_AnswerDoesNotDependOnInvitationOrder(bool listedOwnerInvitesFirst)
    {
        var (listed, _) = await SeedHouseholdAsync(ownerEmail: "friend@example.com");
        var (unlisted, _) = await SeedHouseholdAsync(ownerEmail: "unlisted@example.com");
        foreach (var tenant in listedOwnerInvitesFirst ? new[] { listed, unlisted } : [unlisted, listed])
            await SeedPendingInviteAsync(tenant, "guest@example.com");

        await using var db = Fixture.CreateContext();
        Assert.True(await new ServiceHarness(db, signup: ListOf("friend@example.com")).SignupGate().IsAllowedAsync("guest@example.com"));
    }

    [Fact]
    public async Task Refusal_NamesNoHousehold()
    {
        // The refusal reaches the person refused. It may say their address is not admitted; it must not say
        // who invited them or which household's owner fell short of the list.
        var (unlisted, _) = await SeedHouseholdAsync(ownerEmail: "unlisted@example.com");
        await SeedPendingInviteAsync(unlisted, "guest@example.com");

        await using var db = Fixture.CreateContext();
        var refusal = await Assert.ThrowsAsync<SignupNotAllowedException>(() =>
            new ServiceHarness(db, signup: ListOf("friend@example.com")).UserService().GetOrCreateByEmailAsync("guest@example.com"));

        Assert.DoesNotContain("unlisted@example.com", refusal.Message);
        Assert.DoesNotContain(unlisted.ToString(), refusal.Message);
        Assert.DoesNotContain("Household", refusal.Message); // the seeded tenant's name
    }

    [Fact]
    public async Task GetValidByEmailAcrossTenantsAsync_SeesOtherTenantsInvitations_OnlyBecauseOfItsTag()
    {
        // The gate's read crosses tenants on purpose and is sanctioned per query by its RLS tag (ADR-020). Under
        // some other ambient tenant the tagged read still finds the invitation; the same read without the tag
        // finds nothing — so dropping the tag would silently refuse every invitee rather than leak.
        var owners = new Queue<string>(["elsewhere@example.com", "friend@example.com"]);
        var pair = await TwoTenants.SeedAsync(() => SeedHouseholdAsync(owners.Dequeue()), household => household.TenantId);
        var (ambient, inviting) = (pair.Mine, pair.Other);
        await SeedPendingInviteAsync(inviting, "guest@example.com");

        // Connected as the runtime role, which is subject to RLS (the fixture's own role is exempt).
        await using (var provision = Fixture.CreateTestContext())
            await Rls.RlsTestSetup.ProvisionAsync(provision);
        var options = new DbContextOptionsBuilder<TestAppDbContext>()
            .UseNpgsql(Rls.RlsTestSetup.RuntimeConnectionString(Fixture.ConnectionString)).Options;
        await using var db = new TestAppDbContext(options, new TestCurrentTenant { TenantId = ambient });
        var tagged = await new TenantInvitationRepository(db).GetValidByEmailAcrossTenantsAsync("guest@example.com", DateTimeOffset.UtcNow);
        Assert.Equal(inviting, Assert.Single(tagged).TenantId);

        var untagged = await db.TenantInvitations.IgnoreQueryFilters().Where(i => i.InvitedEmail == "guest@example.com").ToListAsync();
        Assert.Empty(untagged);
    }

    [Fact]
    public async Task InvitationWriter_AndGate_NormaliseTheAddressTheSameWay()
    {
        // The writer stores the invited address normalised and the gate looks it up normalised. If either side
        // changed its rule alone, an invitation typed in one casing would stop admitting the same person.
        var (inviting, owner) = await SeedHouseholdAsync(ownerEmail: "friend@example.com");
        await using (var write = Fixture.CreateContext(inviting))
        {
            var created = await new ServiceHarness(write, currentTenant: new TestCurrentTenant { TenantId = inviting })
                .InvitationService().CreateAsync(inviting, owner, "Guest.Person@Example.COM");
            Assert.NotNull(created.Invitation);
        }

        await using var db = Fixture.CreateContext();
        Assert.True(await new ServiceHarness(db, signup: ListOf("friend@example.com")).SignupGate().IsAllowedAsync("  guest.PERSON@example.com "));
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
        DateTimeOffset? expiresAt = null, string status = InvitationStatuses.Pending, string? tokenHash = null)
    {
        await using var db = Fixture.CreateContext(tenantId);
        db.TenantInvitations.Add(new TenantInvitation
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            InvitedEmail = invitedEmail,
            InvitedByUserId = invitedBy ?? Guid.CreateVersion7(),
            Status = status,
            TokenHash = tokenHash ?? Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddDays(7),
        });
        await db.SaveChangesAsync();
    }
}
