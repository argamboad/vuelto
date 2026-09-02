using Microsoft.EntityFrameworkCore;
using Perezosoft.Api.Services;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Entities;
using Perezosoft.Infrastructure.Audit;
using Perezosoft.Infrastructure.Persistence;
using Perezosoft.Infrastructure.Repositories;

namespace Perezosoft.Api.Tests.Gdpr;

/// <summary>
/// GDPR-2 (ADR-011): account erasure wipes the caller's identity/PII in one transaction while honoring
/// the single-owner invariant — a member is removed (not re-homed) and the tenant survives; a solo
/// owner's tenant is dissolved (with confirmation); an owner with other members must transfer first.
/// The audit trail survives (actor id, never PII). Postgres-backed.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AccountErasureTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task Member_Erasure_RemovesIdentity_KeepsTenant_AndAudits()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = await SeedUserAsync(tenantId, TenantRoles.Owner);
        var (memberId, memberEmail) = await SeedUserWithEmailAsync(tenantId, TenantRoles.Member);
        await SeedIdentityAsync(memberId, memberEmail);

        await using (var db = Fixture.CreateContext(tenantId))
            Assert.Equal(EraseAccountResult.Erased, await NewService(db).EraseAsync(memberId, confirmDissolve: false));

        // Member identity is gone...
        Assert.False(await ExistsAsync<User>(u => u.Id == memberId));
        Assert.Equal(0, await CountAsync<UserLogin>(l => l.UserId == memberId));
        Assert.Equal(0, await CountAsync<RefreshToken>(r => r.UserId == memberId));
        Assert.Equal(0, await CountAsync<LoginToken>(l => l.Email == memberEmail));
        Assert.Equal(0, await CountAsync<UserMfa>(m => m.UserId == memberId));
        Assert.Equal(0, await CountAsync<MfaRecoveryCode>(c => c.UserId == memberId));
        Assert.Equal(0, await CountAsync<Notification>(n => n.UserId == memberId));
        Assert.Equal(0, await CountAsync<NotificationPreference>(p => p.UserId == memberId));
        Assert.Equal(0, await CountAsync<TenantMembership>(m => m.UserId == memberId));
        // ...but the tenant and its owner remain.
        Assert.True(await ExistsAsync<User>(u => u.Id == ownerId));
        Assert.True(await ExistsAsync<Tenant>(t => t.Id == tenantId));
        // ...and the erasure is audited in the surviving tenant, actor id intact.
        Assert.Equal(1, await AuditCountAsync(tenantId, "account.erased", memberId));
    }

    [Fact]
    public async Task SoloOwner_Erasure_WithoutConfirm_RequiresConfirmation_AndErasesNothing()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = await SeedUserAsync(tenantId, TenantRoles.Owner);

        await using (var db = Fixture.CreateContext(tenantId))
            Assert.Equal(EraseAccountResult.DissolveConfirmationRequired,
                await NewService(db).EraseAsync(ownerId, confirmDissolve: false));

        Assert.True(await ExistsAsync<User>(u => u.Id == ownerId));
        Assert.True(await ExistsAsync<Tenant>(t => t.Id == tenantId));
    }

    [Fact]
    public async Task SoloOwner_Erasure_WithConfirm_DissolvesTenant_AndErasesIdentity()
    {
        var tenantId = Guid.CreateVersion7();
        var (ownerId, email) = await SeedUserWithEmailAsync(tenantId, TenantRoles.Owner);
        await SeedIdentityAsync(ownerId, email);
        await SeedWidgetAsync(tenantId);

        await using (var db = Fixture.CreateContext(tenantId))
            Assert.Equal(EraseAccountResult.Erased, await NewService(db).EraseAsync(ownerId, confirmDissolve: true));

        Assert.False(await ExistsAsync<User>(u => u.Id == ownerId));
        Assert.False(await ExistsAsync<Tenant>(t => t.Id == tenantId));
        Assert.Equal(0, await CountIgnoringFiltersAsync<TestWidget>(w => w.TenantId == tenantId));   // domain data wiped
        Assert.Equal(0, await CountAsync<TenantMembership>(m => m.UserId == ownerId));
    }

    [Fact]
    public async Task Owner_WithOtherMembers_MustTransferFirst()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = await SeedUserAsync(tenantId, TenantRoles.Owner);
        await SeedUserAsync(tenantId, TenantRoles.Member);

        await using (var db = Fixture.CreateContext(tenantId))
            Assert.Equal(EraseAccountResult.MustTransferFirst,
                await NewService(db).EraseAsync(ownerId, confirmDissolve: true));

        Assert.True(await ExistsAsync<User>(u => u.Id == ownerId));
        Assert.True(await ExistsAsync<Tenant>(t => t.Id == tenantId));
    }

    [Fact]
    public async Task UnknownUser_ReturnsNoAccount()
    {
        await using var db = Fixture.CreateContext();
        Assert.Equal(EraseAccountResult.NoAccount, await NewService(db).EraseAsync(Guid.CreateVersion7(), false));
    }

    // --- construction ---

    private static AccountErasureService NewService(AppDbContext db)
    {
        var tenants = new TenantRepository(db);
        var dissolution = new TenantDissolutionService(
            new ITenantDataContributor[]
            {
                new TestWidgetDataContributor(new EfRepository<TestWidget>(db)),
                new AuditDataContributor(new EfRepository<AuditEvent>(db)),
            },
            tenants, new TestCurrentTenant());
        return new(tenants,
            new EfUnitOfWork(db),
            dissolution,
            new IUserDataContributor[]
            {
                new MfaUserDataContributor(new EfRepository<UserMfa>(db), new EfRepository<MfaRecoveryCode>(db)),
                new NotificationUserDataContributor(new EfRepository<Notification>(db), new EfRepository<NotificationPreference>(db)),
            },
            new AuditLog(new EfRepository<AuditEvent>(db), TimeProvider.System),
            new EfRepository<User>(db),
            new EfRepository<UserLogin>(db),
            new EfRepository<RefreshToken>(db),
            new EfRepository<LoginToken>(db));
    }

    // --- seeding ---

    private Task<Guid> SeedUserAsync(Guid tenantId, string role) =>
        SeedUserWithEmailAsync(tenantId, role).ContinueWith(t => t.Result.userId);

    private async Task<(Guid userId, string email)> SeedUserWithEmailAsync(Guid tenantId, string role)
    {
        var userId = Guid.CreateVersion7();
        var email = $"u-{userId:N}@x.com";
        await using var db = Fixture.CreateContext();
        if (!await db.Set<Tenant>().AnyAsync(t => t.Id == tenantId))
            db.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = "Tenant" });
        db.Set<User>().Add(new User { Id = userId, Email = email });
        db.Set<TenantMembership>().Add(new TenantMembership { TenantId = tenantId, UserId = userId, Role = role });
        await db.SaveChangesAsync();
        return (userId, email);
    }

    private async Task SeedIdentityAsync(Guid userId, string email)
    {
        await using var db = Fixture.CreateContext();
        db.Set<RefreshToken>().Add(new RefreshToken
        {
            UserId = userId, TokenHash = "hash", IssuedFromIp = "127.0.0.1", Provider = "test",
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        db.Set<UserLogin>().Add(new UserLogin { UserId = userId, Provider = "google", ProviderUserId = "pid-123" });
        db.Set<LoginToken>().Add(new LoginToken
        {
            Email = email, CodeHash = "codehash", Purpose = LoginTokenPurpose.Otp,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10), CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Set<UserMfa>().Add(new UserMfa { UserId = userId, EncryptedSecret = "enc", Enabled = true, EnrolledAt = DateTimeOffset.UtcNow });
        db.Set<MfaRecoveryCode>().Add(new MfaRecoveryCode { UserId = userId, CodeHash = "rc-hash" });
        db.Set<Notification>().Add(new Notification { UserId = userId, Kind = "test", Title = "t", CreatedAt = DateTimeOffset.UtcNow });
        db.Set<NotificationPreference>().Add(new NotificationPreference { UserId = userId });
        await db.SaveChangesAsync();
    }

    private async Task SeedWidgetAsync(Guid tenantId)
    {
        await using var db = Fixture.CreateContext(tenantId);
        db.Set<TestWidget>().Add(new TestWidget { Name = "w", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    // --- assertions ---

    private async Task<bool> ExistsAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate) where T : class
    {
        await using var db = Fixture.CreateContext();
        return await db.Set<T>().AnyAsync(predicate);
    }

    private async Task<int> CountAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate) where T : class
    {
        await using var db = Fixture.CreateContext();
        return await db.Set<T>().CountAsync(predicate);
    }

    private async Task<int> CountIgnoringFiltersAsync<T>(System.Linq.Expressions.Expression<Func<T, bool>> predicate) where T : class
    {
        await using var db = Fixture.CreateContext();
        return await db.Set<T>().IgnoreQueryFilters().CountAsync(predicate);
    }

    private async Task<int> AuditCountAsync(Guid tenantId, string action, Guid actorUserId)
    {
        await using var db = Fixture.CreateContext();
        return await db.Set<AuditEvent>().IgnoreQueryFilters()
            .CountAsync(e => e.TenantId == tenantId && e.Action == action && e.ActorUserId == actorUserId);
    }
}
