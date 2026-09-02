using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Perezosoft.Api.Services;
using Perezosoft.Api.Tests.Infrastructure;
using Perezosoft.Core.Authorization;
using Perezosoft.Core.Entities;
using Perezosoft.Infrastructure.Repositories;

namespace Perezosoft.Api.Tests.Rbac;

/// <summary>
/// RBAC-1 (ADR-009): <see cref="PermissionService"/> resolves the authenticated caller's membership
/// (by the NameIdentifier claim) and answers "does the caller's role grant this permission?" via the
/// <see cref="RolePermissions"/> matrix. Fails closed when there is no principal or no membership.
/// Postgres-backed so the real repository membership lookup is exercised.
/// </summary>
[Collection(PostgresCollection.Name)]
public class PermissionServiceTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    // The three role theories below together pin the COMPLETE role×permission matrix — every
    // Permission member appears in each (v3 TB-ADM-11/12, T45a). MemberData over the enum keeps the
    // owner row future-proof; a NEW permission must then be placed in the admin/member theories
    // explicitly, which is the point: its author decides the row, not a default.
    public static TheoryData<Permission> AllPermissions =>
        [.. Enum.GetValues<Permission>()];

    [Theory]
    [MemberData(nameof(AllPermissions))]
    public async Task Owner_HasEveryPermission(Permission permission)
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = await SeedMembershipAsync(tenantId, TenantRoles.Owner);
        Assert.True(await NewService(ownerId, tenantId).HasAsync(permission));
    }

    [Fact]
    public async Task Authz_ResolvesForTheJwtTenant_AndFailsClosedOnAMismatch()
    {
        // v3 LB-ADM-3: authz must resolve the membership for the caller's JWT tenant. (A DB unique index on
        // UserId enforces one membership per user today, so the "two memberships" case can't occur — but a
        // token whose tenant_id does NOT match the user's membership, e.g. stale/forged, must fail closed
        // rather than silently authorize against the user's other-tenant role.)
        var tenantId = Guid.CreateVersion7();
        var ownerId = await SeedMembershipAsync(tenantId, TenantRoles.Owner);

        Assert.True(await NewService(ownerId, tenantId).HasAsync(Permission.TransferOwnership));          // JWT matches → owner
        Assert.False(await NewService(ownerId, Guid.CreateVersion7()).HasAsync(Permission.TransferOwnership)); // JWT names another tenant → denied
    }

    [Theory]
    [InlineData(Permission.ViewTenant, true)]
    [InlineData(Permission.RenameTenant, true)]
    [InlineData(Permission.ManageMembers, true)]
    [InlineData(Permission.ManageRoles, false)]        // privilege escalation — owner-only
    [InlineData(Permission.ManageBilling, false)]      // financial — owner-only
    [InlineData(Permission.ExportData, false)]         // GDPR export — owner-only
    [InlineData(Permission.TransferOwnership, false)]
    [InlineData(Permission.DissolveTenant, false)]
    [InlineData(Permission.ManageApiKeys, false)]      // programmatic tenant access — owner-only (TB-ADM-12)
    [InlineData(Permission.ManageWebhooks, false)]     // outbound data flow — owner-only (TB-ADM-12)
    public async Task Admin_HasManagementButNotOwnerOnly(Permission permission, bool granted)
    {
        var tenantId = Guid.CreateVersion7();
        await SeedMembershipAsync(tenantId, TenantRoles.Owner);
        var adminId = await SeedMembershipAsync(tenantId, TenantRoles.Admin);
        Assert.Equal(granted, await NewService(adminId, tenantId).HasAsync(permission));
    }

    [Theory]
    [InlineData(Permission.ViewTenant, true)]
    [InlineData(Permission.RenameTenant, false)]
    [InlineData(Permission.ManageMembers, false)]
    [InlineData(Permission.ManageRoles, false)]
    [InlineData(Permission.ManageBilling, false)]
    [InlineData(Permission.ExportData, false)]
    [InlineData(Permission.TransferOwnership, false)]
    [InlineData(Permission.DissolveTenant, false)]
    [InlineData(Permission.ManageApiKeys, false)]
    [InlineData(Permission.ManageWebhooks, false)]
    public async Task Member_HasOnlyViewTenant(Permission permission, bool granted)
    {
        var tenantId = Guid.CreateVersion7();
        await SeedMembershipAsync(tenantId, TenantRoles.Owner);
        var memberId = await SeedMembershipAsync(tenantId, TenantRoles.Member);
        Assert.Equal(granted, await NewService(memberId, tenantId).HasAsync(permission));
    }

    [Fact]
    public void AdminAndMemberTheories_CoverEveryPermission()
    {
        // Completeness canary for the two matrices above: adding a Permission member without placing
        // its admin/member rows must fail HERE, not silently default to untested.
        var admin = RowsOf(nameof(Admin_HasManagementButNotOwnerOnly));
        var member = RowsOf(nameof(Member_HasOnlyViewTenant));
        foreach (var p in Enum.GetValues<Permission>())
        {
            Assert.Contains(p, admin);
            Assert.Contains(p, member);
        }

        static HashSet<Permission> RowsOf(string methodName) =>
            [.. typeof(PermissionServiceTests).GetMethod(methodName)!
                .GetCustomAttributes(typeof(InlineDataAttribute), false)
                .Cast<InlineDataAttribute>()
                .Select(a => (Permission)a.GetData(null!).Single()[0]!)];
    }

    [Fact]
    public async Task NoMembership_FailsClosed()
    {
        Assert.False(await NewService(Guid.CreateVersion7(), Guid.CreateVersion7()).HasAsync(Permission.ViewTenant));
    }

    [Fact]
    public async Task NoAuthenticatedUser_FailsClosed()
    {
        var service = new PermissionService(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new TenantRepository(Fixture.CreateContext()));

        Assert.False(await service.HasAsync(Permission.ViewTenant));
    }

    // The principal carries BOTH the user id and the JWT tenant_id — authz resolves the membership for that
    // tenant (LB-ADM-3), exactly as a real request does.
    private PermissionService NewService(Guid currentUserId, Guid tenantId)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, currentUserId.ToString()), new Claim(JwtClaims.TenantId, tenantId.ToString())],
            authenticationType: "test"));
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } };
        return new PermissionService(accessor, new TenantRepository(Fixture.CreateContext()));
    }

    /// <summary>Adds a (Tenant, User, TenantMembership) and returns the new user id. Not tenant-scoped.</summary>
    private async Task<Guid> SeedMembershipAsync(Guid tenantId, string role)
    {
        var userId = Guid.CreateVersion7();
        await SeedMembershipForAsync(userId, tenantId, role, createUser: true);
        return userId;
    }

    /// <summary>Adds a membership for an existing/new user in a tenant (for the multi-membership case).</summary>
    private async Task SeedMembershipForAsync(Guid userId, Guid tenantId, string role, bool createUser = false)
    {
        await using var db = Fixture.CreateContext();
        if (!await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(db.Set<Tenant>(), t => t.Id == tenantId))
            db.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = "Tenant" });
        if (createUser || !await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.AnyAsync(db.Set<User>(), u => u.Id == userId))
            db.Set<User>().Add(new User { Id = userId, Email = $"u-{userId:N}@x.com" });
        db.Set<TenantMembership>().Add(new TenantMembership { TenantId = tenantId, UserId = userId, Role = role });
        await db.SaveChangesAsync();
    }
}
