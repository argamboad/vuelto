using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Vuelto.Api.Controllers;
using Vuelto.Api.Models;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Audit;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Rbac;

/// <summary>
/// RBAC-2 (ADR-009): the owner-only role-change endpoint (promote/demote between admin and member) and
/// the hardened member-removal that protects the owner now that admins hold <c>ManageMembers</c>.
/// Drives the real <see cref="HouseholdController"/> through the permission seam with a Postgres-backed
/// membership lookup + audit log. Invariants: exactly one owner (owner role never set/cleared here), no
/// self-change, owner-only (ManageRoles), and the change is audited.
/// </summary>
[Collection(PostgresCollection.Name)]
public class MemberRoleManagementTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    // --- role change: happy paths + audit ---

    [Fact]
    public async Task Owner_PromotesMemberToAdmin_NoContent_AndAudited()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = await SeedMembershipAsync(tenantId, TenantRoles.Owner);
        var memberId = await SeedMembershipAsync(tenantId, TenantRoles.Member);

        await using var db = Fixture.CreateContext(tenantId);
        var result = await NewController(db, ownerId)
            .ChangeMemberRole(memberId, new ChangeMemberRoleRequest { Role = TenantRoles.Admin }, default);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(TenantRoles.Admin, await RoleOf(memberId));

        var audit = Assert.Single(await AuditEventsFor(tenantId));
        Assert.Equal("member.role_changed", audit.Action);
        Assert.Equal(ownerId, audit.ActorUserId);
        Assert.Contains(TenantRoles.Admin, audit.Metadata);
        Assert.Contains(TenantRoles.Member, audit.Metadata);
    }

    [Fact]
    public async Task Owner_DemotesAdminToMember_NoContent()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = await SeedMembershipAsync(tenantId, TenantRoles.Owner);
        var adminId = await SeedMembershipAsync(tenantId, TenantRoles.Admin);

        await using var db = Fixture.CreateContext(tenantId);
        var result = await NewController(db, ownerId)
            .ChangeMemberRole(adminId, new ChangeMemberRoleRequest { Role = TenantRoles.Member }, default);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(TenantRoles.Member, await RoleOf(adminId));
    }

    // --- role change: invariants ---

    [Fact]
    public async Task Owner_CannotAssignOwnerRole_Returns400_NoChange_NoAudit()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = await SeedMembershipAsync(tenantId, TenantRoles.Owner);
        var memberId = await SeedMembershipAsync(tenantId, TenantRoles.Member);

        await using var db = Fixture.CreateContext(tenantId);
        var result = await NewController(db, ownerId)
            .ChangeMemberRole(memberId, new ChangeMemberRoleRequest { Role = TenantRoles.Owner }, default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(TenantRoles.Member, await RoleOf(memberId));
        Assert.Empty(await AuditEventsFor(tenantId));
    }

    [Fact]
    public async Task Owner_CannotChangeOwnRole_Returns400()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = await SeedMembershipAsync(tenantId, TenantRoles.Owner);

        await using var db = Fixture.CreateContext(tenantId);
        var result = await NewController(db, ownerId)
            .ChangeMemberRole(ownerId, new ChangeMemberRoleRequest { Role = TenantRoles.Member }, default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(TenantRoles.Owner, await RoleOf(ownerId));
    }

    [Fact]
    public async Task Owner_UnknownRoleString_Returns400()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = await SeedMembershipAsync(tenantId, TenantRoles.Owner);
        var memberId = await SeedMembershipAsync(tenantId, TenantRoles.Member);

        await using var db = Fixture.CreateContext(tenantId);
        var result = await NewController(db, ownerId)
            .ChangeMemberRole(memberId, new ChangeMemberRoleRequest { Role = "superuser" }, default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(TenantRoles.Member, await RoleOf(memberId));
    }

    [Fact]
    public async Task Owner_SetsSameRole_Idempotent_NoContent_NoAudit()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = await SeedMembershipAsync(tenantId, TenantRoles.Owner);
        var memberId = await SeedMembershipAsync(tenantId, TenantRoles.Member);

        await using var db = Fixture.CreateContext(tenantId);
        var result = await NewController(db, ownerId)
            .ChangeMemberRole(memberId, new ChangeMemberRoleRequest { Role = TenantRoles.Member }, default);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(await AuditEventsFor(tenantId));
    }

    [Fact]
    public async Task Owner_TargetNotAMember_Returns404()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = await SeedMembershipAsync(tenantId, TenantRoles.Owner);

        await using var db = Fixture.CreateContext(tenantId);
        var result = await NewController(db, ownerId)
            .ChangeMemberRole(Guid.CreateVersion7(), new ChangeMemberRoleRequest { Role = TenantRoles.Admin }, default);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    // --- role change: authorization (ManageRoles is owner-only) ---

    [Fact]
    public async Task Admin_CannotChangeRoles_Returns403()
    {
        var tenantId = Guid.CreateVersion7();
        await SeedMembershipAsync(tenantId, TenantRoles.Owner);
        var adminId = await SeedMembershipAsync(tenantId, TenantRoles.Admin);
        var memberId = await SeedMembershipAsync(tenantId, TenantRoles.Member);

        await using var db = Fixture.CreateContext(tenantId);
        // ManageRoles gate moved to [RequireTenantPermission] (B9-5); run it as the pipeline would.
        var result = await TenantPermissionGate.RunAsync(
            typeof(HouseholdController), nameof(HouseholdController.ChangeMemberRole), new TenantRepository(db), adminId, tenantId);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
        Assert.Equal(TenantRoles.Member, await RoleOf(memberId));
    }

    [Fact]
    public async Task Member_CannotChangeRoles_Returns403()
    {
        var tenantId = Guid.CreateVersion7();
        await SeedMembershipAsync(tenantId, TenantRoles.Owner);
        var memberId = await SeedMembershipAsync(tenantId, TenantRoles.Member);

        await using var db = Fixture.CreateContext(tenantId);
        // ManageRoles gate moved to [RequireTenantPermission] (B9-5); run it as the pipeline would.
        var result = await TenantPermissionGate.RunAsync(
            typeof(HouseholdController), nameof(HouseholdController.ChangeMemberRole), new TenantRepository(db), memberId, tenantId);

        var obj = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
    }

    // --- member removal hardening: the owner can't be removed by anyone ---

    [Fact]
    public async Task Admin_CannotRemoveOwner_Returns400_OwnerStays()
    {
        var tenantId = Guid.CreateVersion7();
        var ownerId = await SeedMembershipAsync(tenantId, TenantRoles.Owner);
        var adminId = await SeedMembershipAsync(tenantId, TenantRoles.Admin);

        await using var db = Fixture.CreateContext(tenantId);
        var result = await NewController(db, adminId).RemoveMember(ownerId, default);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(TenantRoles.Owner, await RoleOf(ownerId));
    }

    [Fact]
    public async Task Admin_CanRemoveAPlainMember_NoContent()
    {
        var tenantId = Guid.CreateVersion7();
        await SeedMembershipAsync(tenantId, TenantRoles.Owner);
        var adminId = await SeedMembershipAsync(tenantId, TenantRoles.Admin);
        var memberId = await SeedMembershipAsync(tenantId, TenantRoles.Member);

        await using var db = Fixture.CreateContext(tenantId);
        var result = await NewController(db, adminId).RemoveMember(memberId, default);

        Assert.IsType<NoContentResult>(result);
    }

    // --- helpers ---

    private HouseholdController NewController(AppDbContext db, Guid currentUserId)
    {
        var tenants = new TenantRepository(db);
        var audit = new AuditLog(new EfRepository<AuditEvent>(db), TimeProvider.System);
        var dissolution = new TenantDissolutionService([], tenants, new TestCurrentTenant());
        var service = new TenantService(tenants, new EfUnitOfWork(db), dissolution, TimeProvider.System,
            NullLogger<TenantService>.Instance, audit);

        var controller = new HouseholdController(service, tenants, new StubExportService());
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, currentUserId.ToString())], authenticationType: "test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return controller;
    }

    private async Task<string?> RoleOf(Guid userId)
    {
        await using var db = Fixture.CreateContext();
        return (await db.Set<TenantMembership>().FirstOrDefaultAsync(m => m.UserId == userId))?.Role;
    }

    private async Task<List<AuditEvent>> AuditEventsFor(Guid tenantId)
    {
        await using var db = Fixture.CreateContext(tenantId); // ITenantScoped → filtered to this tenant
        return await db.Set<AuditEvent>().ToListAsync();
    }

    /// <summary>Adds a (Tenant, User, TenantMembership) and returns the new user id. Not tenant-scoped.</summary>
    private async Task<Guid> SeedMembershipAsync(Guid tenantId, string role)
    {
        var userId = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext();
        if (!await db.Set<Tenant>().AnyAsync(t => t.Id == tenantId))
            db.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = "Tenant" });
        db.Set<User>().Add(new User { Id = userId, Email = $"u-{userId:N}@x.com" });
        db.Set<TenantMembership>().Add(new TenantMembership { TenantId = tenantId, UserId = userId, Role = role });
        await db.SaveChangesAsync();
        return userId;
    }
}
