using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vuelto.Api.Configuration;
using Vuelto.Api.Controllers;
using Vuelto.Api.Models;
using Vuelto.Api.Services;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Api.Tests.Notify;
using Vuelto.Core.Billing;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Audit;
using Vuelto.Infrastructure.Outbox;
using Vuelto.Infrastructure.Persistence;
using Vuelto.Infrastructure.Repositories;

namespace Vuelto.Api.Tests.Admin;

/// <summary>
/// ADMIN-1 (ADR-014): the staff back-office. Non-staff callers get 403; staff can list tenants and view
/// a tenant's detail. The detail read <b>enters the target tenant</b> (so its scoped data reads through
/// the normal filter) and is <b>audited in that tenant</b>. The controller shares one
/// <c>ICurrentTenant</c>/<c>ITenantContext</c> with its DbContext, exactly as in production.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AdminControllerTests(PostgresFixture fixture) : PostgresTestBase(fixture)
{
    private const string StaffEmail = "staff@corp.com";

    [Fact]
    public async Task NonStaff_ListTenants_Returns403()
    {
        var callerId = await SeedUserAsync("normal@corp.com");
        var (controller, db) = BuildController(callerId);
        await using (db)
        {
            var result = await controller.ListTenants(default);
            var obj = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
        }
    }

    [Fact]
    public async Task ImpersonationToken_AtStaffGate_Returns403_EvenWhenTargetIsStaff()
    {
        // v3 ADM-2: staff A impersonating staff B (both allowlisted) must NOT reach the staff surface —
        // otherwise every audited action attributes B. The caller IS staff by allowlist; the
        // impersonated_by claim alone must trip the gate.
        var staffId = await SeedUserAsync(StaffEmail);
        var (controller, db) = BuildController(staffId, impersonatedBy: Guid.CreateVersion7());
        await using (db)
        {
            var result = await controller.ListTenants(default);
            var obj = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
            Assert.Equal("impersonation_not_allowed", Assert.IsType<ErrorResponse>(obj.Value).Error);
        }
    }

    [Fact]
    public async Task ImpersonationToken_StaffProbe_ReportsNotStaff()
    {
        // The Me probe mirrors the gate: an impersonation session never advertises staff powers (the
        // client already hides the admin UI while impersonating — the server now agrees).
        var staffId = await SeedUserAsync(StaffEmail);
        var (controller, db) = BuildController(staffId, impersonatedBy: Guid.CreateVersion7());
        await using (db)
        {
            var result = await controller.Me(default);
            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.False(Assert.IsType<AdminStatusResponse>(ok.Value).IsStaff);
        }
    }

    [Fact]
    public async Task Staff_ListTenants_ReturnsAllTenantsWithCounts()
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var t1 = await SeedTenantAsync("Alpha", members: 2);
        var t2 = await SeedTenantAsync("Beta", members: 1);

        var (controller, db) = BuildController(staffId);
        await using (db)
        {
            var ok = Assert.IsType<OkObjectResult>(await controller.ListTenants(default));
            var list = Assert.IsAssignableFrom<IReadOnlyList<AdminTenantSummaryResponse>>(ok.Value);
            Assert.Contains(list, t => t.Id == t1 && t.MemberCount == 2);
            Assert.Contains(list, t => t.Id == t2 && t.MemberCount == 1);
        }
    }

    [Fact]
    public async Task Staff_TenantDetail_ReturnsDetail_AndAuditsInTenant()
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var tenantId = await SeedTenantAsync("Gamma", members: 2);
        await SeedSubscriptionAsync(tenantId);

        var (controller, db) = BuildController(staffId);
        await using (db)
        {
            var ok = Assert.IsType<OkObjectResult>(await controller.TenantDetail(tenantId, default));
            var detail = Assert.IsType<AdminTenantDetailResponse>(ok.Value);
            Assert.Equal(2, detail.Members.Count);
            Assert.Equal(SubscriptionStatus.Active, detail.SubscriptionStatus); // "active"
        }

        // The view is audited in the target tenant.
        await using var read = Fixture.CreateContext(tenantId);
        Assert.True(await read.Set<AuditEvent>().AnyAsync(e => e.Action == "admin.tenant.viewed" && e.ActorUserId == staffId));
    }

    [Fact]
    public async Task Staff_TenantDetail_UnknownTenant_Returns404()
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var (controller, db) = BuildController(staffId);
        await using (db)
            Assert.IsType<NotFoundObjectResult>(await controller.TenantDetail(Guid.CreateVersion7(), default));
    }

    // --- staff status probe (UI-4): 200 for any authenticated caller, never 403 ---

    [Fact]
    public async Task Staff_Me_ReturnsIsStaffTrue()
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var (controller, db) = BuildController(staffId);
        await using (db)
        {
            var ok = Assert.IsType<OkObjectResult>(await controller.Me(default));
            Assert.True(Assert.IsType<AdminStatusResponse>(ok.Value).IsStaff);
        }
    }

    [Fact]
    public async Task NonStaff_Me_ReturnsIsStaffFalse_Not403()
    {
        var callerId = await SeedUserAsync("normal@corp.com");
        var (controller, db) = BuildController(callerId);
        await using (db)
        {
            var ok = Assert.IsType<OkObjectResult>(await controller.Me(default));
            Assert.False(Assert.IsType<AdminStatusResponse>(ok.Value).IsStaff);
        }
    }

    // --- ADMIN-2: impersonation ---

    [Fact]
    public async Task NonStaff_Impersonate_Returns403()
    {
        var callerId = await SeedUserAsync("normal@corp.com");
        var targetId = await SeedUserInTenantAsync();
        var (controller, db) = BuildController(callerId);
        await using (db)
        {
            var obj = Assert.IsType<ObjectResult>(await controller.Impersonate(targetId, default));
            Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
        }
    }

    [Fact]
    public async Task Staff_Impersonate_ReturnsShortLivedTaggedToken_AndAudits()
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var (targetId, tenantId) = await SeedUserInTenantWithIdAsync();

        var (controller, db) = BuildController(staffId);
        await using (db)
        {
            var ok = Assert.IsType<OkObjectResult>(await controller.Impersonate(targetId, default));
            var resp = Assert.IsType<ImpersonationResponse>(ok.Value);
            Assert.Equal(900, resp.ExpiresIn); // 15 min, non-refreshable (no refresh token returned)

            var validator = new JwtTokenService(new TestJwtSettings(), TimeProvider.System, NullLogger<JwtTokenService>.Instance);
            var principal = validator.ValidateToken(resp.AccessToken);
            Assert.NotNull(principal);
            Assert.Equal(targetId.ToString(), principal!.FindFirstValue(ClaimTypes.NameIdentifier));
            Assert.Equal(staffId.ToString(), principal.FindFirstValue(JwtClaims.ImpersonatedBy));
            Assert.Equal(tenantId.ToString(), principal.FindFirstValue(JwtClaims.TenantId));
        }

        await using var read = Fixture.CreateContext(tenantId);
        Assert.True(await read.Set<AuditEvent>().AnyAsync(e => e.Action == "admin.impersonation.started" && e.ActorUserId == staffId));
    }

    [Fact]
    public async Task Staff_Impersonate_UnknownUser_Returns404()
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var (controller, db) = BuildController(staffId);
        await using (db)
            Assert.IsType<NotFoundObjectResult>(await controller.Impersonate(Guid.CreateVersion7(), default));
    }

    // --- ADMIN-3: announcements ---

    [Fact]
    public async Task NonStaff_Announce_Returns403()
    {
        var callerId = await SeedUserAsync("normal@corp.com");
        var tenantId = await SeedTenantAsync("Delta", members: 1);
        var (controller, db) = BuildController(callerId);
        await using (db)
        {
            var obj = Assert.IsType<ObjectResult>(
                await controller.Announce(tenantId, new AdminAnnounceRequest { Title = "t", Body = "b" }, default));
            Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
        }
    }

    [Fact]
    public async Task Staff_Announce_NotifiesAllMembers_AndAuditsInTenant()
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var tenantId = await SeedTenantAsync("Epsilon", members: 2);
        var title = $"Maintenance {Guid.NewGuid():N}";

        var (controller, db) = BuildController(staffId);
        await using (db)
        {
            var ok = Assert.IsType<OkObjectResult>(await controller.Announce(
                tenantId, new AdminAnnounceRequest { Title = title, Body = "Scheduled downtime tonight." }, default));
            Assert.Equal(2, Assert.IsType<AdminAnnounceResponse>(ok.Value).NotifiedCount);
        }

        // One in-app notification per member (notifications are per-user, not tenant-scoped)...
        await using var read = Fixture.CreateContext(tenantId);
        Assert.Equal(2, await read.Set<Notification>().CountAsync(n => n.Title == title && n.Kind == "announcement"));

        // ...and the announcement is audited in the target tenant.
        Assert.True(await read.Set<AuditEvent>().AnyAsync(
            e => e.Action == "admin.announcement.sent" && e.ActorUserId == staffId));
    }

    [Fact]
    public async Task Staff_Announce_UnknownTenant_Returns404()
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var (controller, db) = BuildController(staffId);
        await using (db)
            Assert.IsType<NotFoundObjectResult>(await controller.Announce(
                Guid.CreateVersion7(), new AdminAnnounceRequest { Title = "t", Body = "b" }, default));
    }

    [Theory]
    [InlineData(null, "b")]
    [InlineData("  ", "b")]
    [InlineData("t", null)]
    [InlineData("t", "")]
    public async Task Staff_Announce_MissingTitleOrBody_Returns400(string? title, string? body)
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var tenantId = await SeedTenantAsync("Zeta", members: 1);
        var (controller, db) = BuildController(staffId);
        await using (db)
            Assert.IsType<BadRequestObjectResult>(await controller.Announce(
                tenantId, new AdminAnnounceRequest { Title = title, Body = body }, default));
    }

    [Fact]
    public async Task Staff_Announce_OverlongTitleOrBody_Returns400()
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var tenantId = await SeedTenantAsync("Eta", members: 1);
        var (controller, db) = BuildController(staffId);
        await using (db)
        {
            Assert.IsType<BadRequestObjectResult>(await controller.Announce(
                tenantId, new AdminAnnounceRequest { Title = new string('x', 201), Body = "b" }, default));
            Assert.IsType<BadRequestObjectResult>(await controller.Announce(
                tenantId, new AdminAnnounceRequest { Title = "t", Body = new string('x', 2001) }, default));
        }
    }

    [Fact]
    public async Task Staff_Announce_WithUserIds_NotifiesOnlyThoseMembers()
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var tenantId = await SeedTenantAsync("Theta", members: 3);
        var title = $"Targeted {Guid.NewGuid():N}";

        List<Guid> memberIds;
        await using (var seed = Fixture.CreateContext(tenantId))
            memberIds = await seed.Set<TenantMembership>()
                .Where(m => m.TenantId == tenantId).Select(m => m.UserId).ToListAsync();
        var target = memberIds[0];

        var (controller, db) = BuildController(staffId);
        await using (db)
        {
            var ok = Assert.IsType<OkObjectResult>(await controller.Announce(
                tenantId, new AdminAnnounceRequest { Title = title, Body = "Just you.", UserIds = [target] }, default));
            Assert.Equal(1, Assert.IsType<AdminAnnounceResponse>(ok.Value).NotifiedCount);
        }

        await using var read = Fixture.CreateContext(tenantId);
        var notified = await read.Set<Notification>().Where(n => n.Title == title).Select(n => n.UserId).ToListAsync();
        Assert.Equal(new[] { target }, notified); // exactly the targeted member, nobody else
    }

    [Fact]
    public async Task Staff_Announce_WithEmptyUserIds_NotifiesNoOne()
    {
        // v3 LB-ADM-2: a present-but-empty user_ids is an explicit "target this (empty) set" — it must reach
        // NO ONE, not fall through to the whole tenant (the max-blast-radius bug).
        var staffId = await SeedUserAsync(StaffEmail);
        var tenantId = await SeedTenantAsync("ThetaEmpty", members: 3);
        var title = $"Empty {Guid.NewGuid():N}";

        var (controller, db) = BuildController(staffId);
        await using (db)
        {
            var ok = Assert.IsType<OkObjectResult>(await controller.Announce(
                tenantId, new AdminAnnounceRequest { Title = title, Body = "b", UserIds = [] }, default));
            Assert.Equal(0, Assert.IsType<AdminAnnounceResponse>(ok.Value).NotifiedCount);
        }

        await using var read = Fixture.CreateContext(tenantId);
        Assert.Equal(0, await read.Set<Notification>().CountAsync(n => n.Title == title));
    }

    [Fact]
    public async Task Staff_Announce_WithForeignOrUnknownUserIds_NotifiesNoOne_Anywhere()
    {
        // v3 TB-ADM-8 (T45a): user_ids is intersected with ACTUAL membership of the target tenant, so an
        // id belonging to another tenant (or a random guid) must reach no one — neither in the target
        // tenant nor, crucially, in the foreign user's own tenant.
        var staffId = await SeedUserAsync(StaffEmail);
        var targetTenant = await SeedTenantAsync("Xi", members: 2);
        var otherTenant = await SeedTenantAsync("Omicron", members: 1);
        var title = $"Foreign {Guid.NewGuid():N}";

        Guid foreignUserId;
        await using (var seed = Fixture.CreateContext(otherTenant))
            foreignUserId = await seed.Set<TenantMembership>()
                .Where(m => m.TenantId == otherTenant).Select(m => m.UserId).SingleAsync();

        var (controller, db) = BuildController(staffId);
        await using (db)
        {
            var ok = Assert.IsType<OkObjectResult>(await controller.Announce(
                targetTenant,
                new AdminAnnounceRequest { Title = title, Body = "b", UserIds = [foreignUserId, Guid.CreateVersion7()] },
                default));
            Assert.Equal(0, Assert.IsType<AdminAnnounceResponse>(ok.Value).NotifiedCount);
        }

        // No rows with this title exist ANYWHERE — not in the target tenant, not in the foreign one.
        await using var read = Fixture.CreateContext();
        Assert.Equal(0, await read.Set<Notification>().IgnoreQueryFilters().CountAsync(n => n.Title == title));
    }

    [Fact]
    public async Task NonStaff_AnnounceAll_Returns403()
    {
        var callerId = await SeedUserAsync("normal2@corp.com");
        var (controller, db) = BuildController(callerId);
        await using (db)
        {
            var obj = Assert.IsType<ObjectResult>(
                await controller.AnnounceAll(new AdminAnnounceRequest { Title = "t", Body = "b" }, default));
            Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
        }
    }

    [Fact]
    public async Task Staff_AnnounceAll_EnqueuesBroadcastOutboxMessage()
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var (controller, db) = BuildController(staffId);
        await using (db)
        {
            var accepted = Assert.IsType<AcceptedResult>(await controller.AnnounceAll(
                new AdminAnnounceRequest { Title = "All hands", Body = "Please read." }, default));
            Assert.Equal("queued", Assert.IsType<AdminBroadcastResponse>(accepted.Value).Status);
        }

        await using var read = Fixture.CreateContext();
        Assert.True(await read.Set<OutboxMessage>()
            .AnyAsync(m => m.Type == AdminBroadcastOutboxHandler.MessageType && m.Status == OutboxStatus.Pending));
    }

    [Fact]
    public async Task AdminBroadcastHandler_NotifiesEveryUser()
    {
        var u1 = await SeedUserAsync($"bc1-{Guid.NewGuid():N}@x.com");
        var u2 = await SeedUserAsync($"bc2-{Guid.NewGuid():N}@x.com");
        var title = $"Broadcast {Guid.NewGuid():N}";

        await using var db = Fixture.CreateContext();
        var notifications = new NotificationService(
            new EfRepository<Notification>(db), new EfRepository<NotificationPreference>(db),
            new UserRepository(db), new CapturingEmailSender(), TimeProvider.System);
        var handler = new AdminBroadcastOutboxHandler(new UserRepository(db), notifications);

        await handler.HandleAsync(new OutboxMessage
        {
            Type = AdminBroadcastOutboxHandler.MessageType,
            Payload = JsonSerializer.Serialize(new AdminBroadcastPayload(title, "Everyone gets this.", Guid.CreateVersion7())),
        });
        await db.SaveChangesAsync(); // handler stages; the outbox processor's tx commits in prod

        var recipients = await db.Set<Notification>()
            .Where(n => n.Title == title).Select(n => n.UserId).ToListAsync();
        Assert.Contains(u1, recipients);
        Assert.Contains(u2, recipients); // fan-out reached every user
    }

    [Fact]
    public async Task AdminBroadcast_ThroughTheRealProcessor_IsExactlyOnce_EvenWhenPolledAgain()
    {
        // v3 TB-BILL backfill (T45b): the broadcast's idempotency is BY CONSTRUCTION — the per-user
        // rows commit atomically with the message's Sent flip inside the processor's transaction. Pin
        // it at the machinery level: run the real OutboxProcessor over the broadcast message twice;
        // the second pass must find nothing to do, and every user has exactly ONE notification row.
        var u1 = await SeedUserAsync($"bc-a-{Guid.NewGuid():N}@x.com");
        var u2 = await SeedUserAsync($"bc-b-{Guid.NewGuid():N}@x.com");
        var title = $"OnceOnly {Guid.NewGuid():N}";

        await using (var seed = Fixture.CreateContext())
        {
            seed.Set<OutboxMessage>().Add(new OutboxMessage
            {
                Type = AdminBroadcastOutboxHandler.MessageType,
                Payload = JsonSerializer.Serialize(new AdminBroadcastPayload(title, "b", Guid.CreateVersion7())),
                Status = OutboxStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        async Task<int> PollAsync()
        {
            await using var db = Fixture.CreateContext();
            var handler = new AdminBroadcastOutboxHandler(new UserRepository(db), new NotificationService(
                new EfRepository<Notification>(db), new EfRepository<NotificationPreference>(db),
                new UserRepository(db), _email, TimeProvider.System));
            return await new Vuelto.Infrastructure.Outbox.OutboxProcessor(
                db, [handler], TimeProvider.System, new Vuelto.Infrastructure.Outbox.OutboxOptions(),
                NullLogger<Vuelto.Infrastructure.Outbox.OutboxProcessor>.Instance).ProcessDueAsync();
        }

        Assert.Equal(1, await PollAsync()); // delivered + Sent, atomically
        Assert.Equal(0, await PollAsync()); // an at-least-once re-poll finds nothing pending

        await using var read = Fixture.CreateContext();
        Assert.Equal(1, await read.Set<Notification>().IgnoreQueryFilters().CountAsync(n => n.Title == title && n.UserId == u1));
        Assert.Equal(1, await read.Set<Notification>().IgnoreQueryFilters().CountAsync(n => n.Title == title && n.UserId == u2));
    }

    [Fact]
    public async Task NonStaff_SetSubscription_Returns403()
    {
        var callerId = await SeedUserAsync("normal3@corp.com");
        var tenantId = await SeedTenantAsync("Iota", members: 1);
        var (controller, db) = BuildController(callerId);
        await using (db)
        {
            var obj = Assert.IsType<ObjectResult>(await controller.SetSubscription(
                tenantId, new AdminSetSubscriptionRequest { PlanKey = PlanKeys.Pro }, default));
            Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
        }
    }

    [Fact]
    public async Task Staff_CompPro_CreatesActiveProjection_NoProviderIds_NeverLapses_AndAudits()
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var tenantId = await SeedTenantAsync("Kappa", members: 1);

        var (controller, db) = BuildController(staffId);
        await using (db)
        {
            var ok = Assert.IsType<OkObjectResult>(await controller.SetSubscription(
                tenantId, new AdminSetSubscriptionRequest { PlanKey = PlanKeys.Pro }, default));
            var body = Assert.IsType<AdminSubscriptionResponse>(ok.Value);
            Assert.Equal(PlanKeys.Pro, body.PlanKey);
            Assert.Equal(SubscriptionStatus.Active, body.Status);
        }

        await using var read = Fixture.CreateContext(tenantId);
        var sub = await read.Set<Subscription>().SingleAsync(s => s.TenantId == tenantId);
        Assert.Equal(PlanKeys.Pro, sub.PlanKey);
        Assert.Equal(SubscriptionStatus.Active, sub.Status);
        Assert.Null(sub.StripeSubscriptionId); // a comp, not a provider subscription
        Assert.Null(sub.CurrentPeriodEnd);     // never lapses
        Assert.True(await read.Set<AuditEvent>().AnyAsync(
            e => e.Action == "admin.subscription.comped" && e.ActorUserId == staffId));
    }

    [Fact]
    public async Task Staff_CompForTenantA_LeavesTenantBUntouched()
    {
        // v3 TB-TEN-10 (T45a): the comp is an ENUMERATED cross-tenant write (ADR-021) that enters the
        // target tenant — it must land in that tenant only. Tenant B keeps its own subscription
        // byte-for-byte, and A's new projection is stamped to A.
        var staffId = await SeedUserAsync(StaffEmail);
        var tenantA = await SeedTenantAsync("Pi", members: 1);
        var tenantB = await SeedTenantAsync("Rho", members: 1);
        await SeedSubscriptionAsync(tenantB, stripeSubscriptionId: "sub_b_live", status: SubscriptionStatus.PastDue);

        var (controller, db) = BuildController(staffId);
        await using (db)
            Assert.IsType<OkObjectResult>(await controller.SetSubscription(
                tenantA, new AdminSetSubscriptionRequest { PlanKey = PlanKeys.Pro }, default));

        await using var read = Fixture.CreateContext();
        var subA = await read.Set<Subscription>().IgnoreQueryFilters().SingleAsync(s => s.TenantId == tenantA);
        Assert.Equal(SubscriptionStatus.Active, subA.Status); // the comp, in A
        var subB = await read.Set<Subscription>().IgnoreQueryFilters().SingleAsync(s => s.TenantId == tenantB);
        Assert.Equal(SubscriptionStatus.PastDue, subB.Status);    // B untouched —
        Assert.Equal("sub_b_live", subB.StripeSubscriptionId);    // — provider linkage intact
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("free")]     // revert is DELETE, not PUT free
    [InlineData("platinum")] // unknown plan must not silently comp Free
    public async Task Staff_CompUnknownOrFreePlan_Returns400(string? planKey)
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var tenantId = await SeedTenantAsync("Lambda", members: 1);
        var (controller, db) = BuildController(staffId);
        await using (db)
            Assert.IsType<BadRequestObjectResult>(await controller.SetSubscription(
                tenantId, new AdminSetSubscriptionRequest { PlanKey = planKey }, default));
    }

    [Fact]
    public async Task Staff_CompOrRevert_ProviderManagedSubscription_Returns409()
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var tenantId = await SeedTenantAsync("Mu", members: 1);
        await SeedSubscriptionAsync(tenantId, stripeSubscriptionId: "sub_live_123"); // real Stripe sub

        var (controller, db) = BuildController(staffId);
        await using (db)
        {
            // Stripe is the source of truth for real money (ADR-006): staff must not clobber it.
            Assert.IsType<ConflictObjectResult>(await controller.SetSubscription(
                tenantId, new AdminSetSubscriptionRequest { PlanKey = PlanKeys.Pro }, default));
            Assert.IsType<ConflictObjectResult>(await controller.RemoveSubscription(tenantId, default));
        }

        await using var read = Fixture.CreateContext(tenantId);
        Assert.Equal("sub_live_123", (await read.Set<Subscription>().SingleAsync(s => s.TenantId == tenantId)).StripeSubscriptionId);
    }

    [Fact]
    public async Task Staff_CompCanceledSubscription_IsAllowed_EvenThoughItKeptItsStripeId()
    {
        // v3 ADM-5: a canceled Stripe sub keeps its id forever; keying the 409 on id-PRESENCE would lock a
        // churned tenant (the exact goodwill-comp target) out permanently. Liveness, not id-presence.
        var staffId = await SeedUserAsync(StaffEmail);
        var tenantId = await SeedTenantAsync("MuCanceled", members: 1);
        await SeedSubscriptionAsync(tenantId, stripeSubscriptionId: "sub_dead_123", status: SubscriptionStatus.Canceled);

        var (controller, db) = BuildController(staffId);
        await using (db)
        {
            var ok = Assert.IsType<OkObjectResult>(await controller.SetSubscription(
                tenantId, new AdminSetSubscriptionRequest { PlanKey = PlanKeys.Pro }, default));
            Assert.Equal(SubscriptionStatus.Active, Assert.IsType<AdminSubscriptionResponse>(ok.Value).Status);
        }
    }

    [Fact]
    public async Task Staff_RevertCanceledSubscription_IsAllowed_NotBlockedByTheDeadStripeId()
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var tenantId = await SeedTenantAsync("MuCanceledRevert", members: 1);
        await SeedSubscriptionAsync(tenantId, stripeSubscriptionId: "sub_dead_456", status: SubscriptionStatus.Canceled);

        var (controller, db) = BuildController(staffId);
        await using (db)
            Assert.IsType<NoContentResult>(await controller.RemoveSubscription(tenantId, default)); // cleaned up, not 409

        await using var read = Fixture.CreateContext(tenantId);
        Assert.False(await read.Set<Subscription>().AnyAsync(s => s.TenantId == tenantId)); // gone
    }

    [Fact]
    public async Task Staff_Revert_DeletesCompedProjection_AndAudits_ThenIdempotent()
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var tenantId = await SeedTenantAsync("Nu", members: 1);
        await SeedSubscriptionAsync(tenantId); // comp-style: no provider subscription id

        var (controller, db) = BuildController(staffId);
        await using (db)
        {
            Assert.IsType<NoContentResult>(await controller.RemoveSubscription(tenantId, default));
            // Absence ⇒ Free already — a second revert is a harmless no-op.
            Assert.IsType<NoContentResult>(await controller.RemoveSubscription(tenantId, default));
        }

        await using var read = Fixture.CreateContext(tenantId);
        Assert.False(await read.Set<Subscription>().AnyAsync(s => s.TenantId == tenantId));
        Assert.True(await read.Set<AuditEvent>().AnyAsync(
            e => e.Action == "admin.subscription.reverted" && e.ActorUserId == staffId));
    }

    [Fact]
    public async Task Staff_SetSubscription_UnknownTenant_Returns404()
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var (controller, db) = BuildController(staffId);
        await using (db)
            Assert.IsType<NotFoundObjectResult>(await controller.SetSubscription(
                Guid.CreateVersion7(), new AdminSetSubscriptionRequest { PlanKey = PlanKeys.Pro }, default));
    }

    // --- ADR-021 addendum: staff MFA reset (recovery when authenticator + codes are lost) ---

    [Fact]
    public async Task NonStaff_ResetMfa_Returns403_AndWipesNothing()
    {
        var callerId = await SeedUserAsync("normal4@corp.com");
        var (targetId, _) = await SeedUserInTenantWithIdAsync();
        await SeedMfaAsync(targetId);

        var (controller, db) = BuildController(callerId);
        await using (db)
        {
            var obj = Assert.IsType<ObjectResult>(await controller.ResetMfa(targetId, default));
            Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
        }

        await using var read = Fixture.CreateContext();
        Assert.True(await read.Set<UserMfa>().AnyAsync(m => m.UserId == targetId && m.Enabled));
        Assert.True(await read.Set<MfaRecoveryCode>().AnyAsync(c => c.UserId == targetId));
    }

    [Fact]
    public async Task Staff_ResetMfa_WipesMfa_AuditsInTargetTenant_AndNotifies()
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var (targetId, tenantId) = await SeedUserInTenantWithIdAsync();
        await SeedMfaAsync(targetId);

        var (controller, db) = BuildController(staffId);
        await using (db)
            Assert.IsType<NoContentResult>(await controller.ResetMfa(targetId, default));

        // The wipe: secret and recovery codes are gone (the user can re-enroll from scratch).
        await using var read = Fixture.CreateContext(tenantId);
        Assert.False(await read.Set<UserMfa>().AnyAsync(m => m.UserId == targetId));
        Assert.False(await read.Set<MfaRecoveryCode>().AnyAsync(c => c.UserId == targetId));

        // Loud, in-tenant audit — like impersonation, the tenant can see staff touched the account.
        Assert.True(await read.Set<AuditEvent>().AnyAsync(e =>
            e.Action == "admin.mfa.reset" && e.ActorUserId == staffId && e.EntityId == targetId.ToString()));

        // The affected user is told through the normal fan-out: in-app row + email copy.
        Assert.True(await read.Set<Notification>().AnyAsync(n =>
            n.UserId == targetId && n.Kind == AdminMfaResetNotification.Kind));
        Assert.Contains(_email.Sent, m => m.To == $"t-{targetId:N}@x.com");
    }

    [Fact]
    public async Task Staff_ResetMfa_RevokesTargetsRefreshTokens()
    {
        // v3 ADM-7: a reset is also compromise recovery — an attacker's live sessions must not survive the
        // second-factor wipe.
        var staffId = await SeedUserAsync(StaffEmail);
        var (targetId, tenantId) = await SeedUserInTenantWithIdAsync();
        await SeedMfaAsync(targetId);
        await SeedRefreshTokenAsync(targetId);

        var (controller, db) = BuildController(staffId);
        await using (db)
            Assert.IsType<NoContentResult>(await controller.ResetMfa(targetId, default));

        await using var read = Fixture.CreateContext(tenantId);
        Assert.False(await read.Set<RefreshToken>().AnyAsync(t => t.UserId == targetId && !t.IsRevoked)); // all revoked
    }

    [Fact]
    public async Task Staff_AnnounceAll_Payload_CarriesTheActingStaffId()
    {
        // v3 ADM-6: the platform-wide broadcast has no in-tenant audit, so the durable outbox message must
        // attribute the acting staff.
        var staffId = await SeedUserAsync(StaffEmail);
        var (controller, db) = BuildController(staffId);
        await using (db)
            Assert.IsType<AcceptedResult>(await controller.AnnounceAll(
                new AdminAnnounceRequest { Title = "All hands", Body = "Please read." }, default));

        await using var read = Fixture.CreateContext();
        var message = await read.Set<OutboxMessage>().SingleAsync(m => m.Type == AdminBroadcastOutboxHandler.MessageType);
        var payload = System.Text.Json.JsonSerializer.Deserialize<AdminBroadcastPayload>(message.Payload)!;
        Assert.Equal(staffId, payload.StaffUserId);
    }

    [Fact]
    public async Task Staff_ResetMfa_UnknownUser_Returns404()
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var (controller, db) = BuildController(staffId);
        await using (db)
            Assert.IsType<NotFoundObjectResult>(await controller.ResetMfa(Guid.CreateVersion7(), default));
    }

    [Fact]
    public async Task Staff_ResetMfa_NoMfaState_IsNoOp_WithoutAuditOrNotification()
    {
        var staffId = await SeedUserAsync(StaffEmail);
        var (targetId, tenantId) = await SeedUserInTenantWithIdAsync(); // never enrolled

        var (controller, db) = BuildController(staffId);
        await using (db)
            Assert.IsType<NoContentResult>(await controller.ResetMfa(targetId, default)); // idempotent

        // Nothing was reset, so nothing is audited and nobody is alarmed.
        await using var read = Fixture.CreateContext(tenantId);
        Assert.False(await read.Set<AuditEvent>().AnyAsync(e =>
            e.Action == "admin.mfa.reset" && e.EntityId == targetId.ToString()));
        Assert.False(await read.Set<Notification>().AnyAsync(n => n.UserId == targetId));
        Assert.Empty(_email.Sent);
    }

    // --- construction (shared tenant context between controller + DbContext) ---

    // Captures the email copies NotifyAsync sends, so tests can assert delivery (e.g. the MFA reset).
    private readonly CapturingEmailSender _email = new();

    private (AdminController controller, AppDbContext db) BuildController(Guid callerId, Guid? impersonatedBy = null)
    {
        var ctx = new HttpCurrentTenant(new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(Fixture.ConnectionString).Options;
        var db = new AppDbContext(options, ctx);

        var staff = new PlatformStaffService(new UserRepository(db), Options.Create(new PlatformAdminSettings { StaffEmails = [StaffEmail] }));
        var notifications = new NotificationService(
            new EfRepository<Notification>(db), new EfRepository<NotificationPreference>(db),
            new UserRepository(db), _email, TimeProvider.System);
        var mfaService = new MfaService(
            new EfRepository<UserMfa>(db), new EfRepository<MfaRecoveryCode>(db), new UserRepository(db),
            new EphemeralDataProtectionProvider(), new RecoveryCodeHasher(new TestJwtSettings()), new TestMfaSettings(), TimeProvider.System);
        var controller = new AdminController(
            staff, new TenantRepository(db), ctx,
            new AuditLog(new EfRepository<AuditEvent>(db), TimeProvider.System),
            new EfRepository<Subscription>(db), new EfRepository<AuditEvent>(db),
            new UserRepository(db),
            new JwtTokenService(new TestJwtSettings(), TimeProvider.System, NullLogger<JwtTokenService>.Instance),
            notifications, mfaService,
            new RefreshTokenService(new RefreshTokenRepository(db, TimeProvider.System), new TokenGenerator(),
                new TokenHasher(), new TestRefreshSettings(), TimeProvider.System),
            new EfOutbox(db, TimeProvider.System), new EfUnitOfWork(db),
            NullLogger<AdminController>.Instance, TimeProvider.System);

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, callerId.ToString()) };
        if (impersonatedBy is { } staffId)
            claims.Add(new Claim(JwtClaims.ImpersonatedBy, staffId.ToString())); // an impersonation session
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } };
        return (controller, db);
    }

    // --- seeding ---

    private async Task<Guid> SeedUserAsync(string email)
    {
        var user = new User { Id = Guid.CreateVersion7(), Email = email };
        await using var db = Fixture.CreateContext();
        db.Set<User>().Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<Guid> SeedTenantAsync(string name, int members)
    {
        var tenantId = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext();
        db.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = name, CreatedAt = DateTimeOffset.UtcNow });
        for (var i = 0; i < members; i++)
        {
            var uid = Guid.CreateVersion7();
            db.Set<User>().Add(new User { Id = uid, Email = $"m-{uid:N}@x.com" });
            db.Set<TenantMembership>().Add(new TenantMembership { TenantId = tenantId, UserId = uid, Role = i == 0 ? TenantRoles.Owner : TenantRoles.Member });
        }
        await db.SaveChangesAsync();
        return tenantId;
    }

    private async Task<Guid> SeedUserInTenantAsync() => (await SeedUserInTenantWithIdAsync()).userId;

    private async Task<(Guid userId, Guid tenantId)> SeedUserInTenantWithIdAsync()
    {
        var tenantId = Guid.CreateVersion7();
        var userId = Guid.CreateVersion7();
        await using var db = Fixture.CreateContext();
        db.Set<Tenant>().Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
        db.Set<User>().Add(new User { Id = userId, Email = $"t-{userId:N}@x.com", DisplayName = "Target" });
        db.Set<TenantMembership>().Add(new TenantMembership { TenantId = tenantId, UserId = userId, Role = TenantRoles.Owner });
        await db.SaveChangesAsync();
        return (userId, tenantId);
    }

    /// <summary>Enabled MFA state for a user: one UserMfa row + a recovery code (reset never decrypts,
    /// so placeholder ciphertext/hash are enough).</summary>
    private async Task SeedMfaAsync(Guid userId)
    {
        await using var db = Fixture.CreateContext();
        db.Set<UserMfa>().Add(new UserMfa { UserId = userId, EncryptedSecret = "enc", Enabled = true, EnrolledAt = DateTimeOffset.UtcNow });
        db.Set<MfaRecoveryCode>().Add(new MfaRecoveryCode { UserId = userId, CodeHash = "hash" });
        await db.SaveChangesAsync();
    }

    private async Task SeedRefreshTokenAsync(Guid userId)
    {
        await using var db = Fixture.CreateContext();
        db.Set<RefreshToken>().Add(new RefreshToken
        {
            UserId = userId, TokenHash = Guid.NewGuid().ToString("N"), IssuedFromIp = "127.0.0.1", Provider = "test",
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30), IsRevoked = false,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedSubscriptionAsync(Guid tenantId, string? stripeSubscriptionId = null, string? status = null)
    {
        await using var db = Fixture.CreateContext(tenantId); // interceptor stamps TenantId
        db.Set<Subscription>().Add(new Subscription
        {
            PlanKey = PlanKeys.Pro,
            Status = status ?? SubscriptionStatus.Active,
            StripeCustomerId = "cus_x",
            StripeSubscriptionId = stripeSubscriptionId,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
