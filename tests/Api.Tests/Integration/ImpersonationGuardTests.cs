using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Api.Tests.Infrastructure;
using Vuelto.Core.Entities;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Api.Tests.Integration;

/// <summary>
/// v3 audit ADM-8 + LB-ADM-1, at the real HTTP boundary (real impersonation tokens minted by the app's
/// own token service):
/// <list type="bullet">
/// <item><b>ADM-8</b> — account-preference writes reject impersonation tokens server-side; the
/// no-write-while-impersonating guard (ADR-022) is no longer client-only.</item>
/// <item><b>LB-ADM-1</b> — a tenant write performed during an impersonation session stamps
/// <c>AuditEvent.ImpersonatedBy</c> with the REAL actor, end-to-end: claim → <c>HttpCurrentImpersonation</c>
/// → <c>AuditLog</c> → column. Before the fix every such write was indistinguishable from the user
/// acting alone.</item>
/// </list>
/// </summary>
[Collection(IntegrationCollection.Name)]
public class ImpersonationGuardTests(IntegrationTestFactory factory)
{
    private readonly IntegrationTestFactory _factory = factory;

    [Theory]
    [InlineData("/api/auth/theme", """{"theme":"dark"}""")]
    [InlineData("/api/auth/locale", """{"locale":"en"}""")]
    public async Task PrefWrites_UnderImpersonation_Return403(string url, string body)
    {
        var staff = await _factory.SeedUserAsync();
        var target = await _factory.SeedUserAsync();

        var resp = await _factory.CreateImpersonatingClientFor(staff, target)
            .PutAsync(url, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("impersonation_not_allowed", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PrefWrite_WithANormalToken_StillSucceeds()
    {
        var user = await _factory.SeedUserAsync();
        var resp = await _factory.CreateClientFor(user)
            .PutAsJsonAsync("/api/auth/theme", new { theme = "dark" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task ImpersonatedWrite_RecordsActingStaff_OnTheAuditRow()
    {
        var staff = await _factory.SeedUserAsync();
        var owner = await _factory.SeedUserAsync(TenantRoles.Owner);
        var member = await SeedMemberIntoAsync(owner.TenantId);

        // Staff impersonates the owner and performs an audited tenant write (owner-only role change).
        var resp = await _factory.CreateImpersonatingClientFor(staff, owner)
            .PutAsJsonAsync($"/api/household/members/{member}/role", new { role = TenantRoles.Admin });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        // The audit row shows the tenant's natural history (actor = the impersonated owner) AND the real
        // hands on the keyboard.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = Assert.Single(await db.Set<AuditEvent>().IgnoreQueryFilters()
            .Where(e => e.TenantId == owner.TenantId && e.Action == "member.role_changed").ToListAsync());
        Assert.Equal(owner.UserId, row.ActorUserId);
        Assert.Equal(staff.UserId, row.ImpersonatedBy);
    }

    [Fact]
    public async Task NormalWrite_LeavesImpersonatedByNull()
    {
        var owner = await _factory.SeedUserAsync(TenantRoles.Owner);
        var member = await SeedMemberIntoAsync(owner.TenantId);

        var resp = await _factory.CreateClientFor(owner)
            .PutAsJsonAsync($"/api/household/members/{member}/role", new { role = TenantRoles.Admin });
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var row = Assert.Single(await db.Set<AuditEvent>().IgnoreQueryFilters()
            .Where(e => e.TenantId == owner.TenantId && e.Action == "member.role_changed").ToListAsync());
        Assert.Null(row.ImpersonatedBy);
    }

    /// <summary>Seeds an extra plain member into an existing tenant; returns the user id.</summary>
    private async Task<Guid> SeedMemberIntoAsync(Guid tenantId)
    {
        var user = new User
        {
            Email = $"member-{Guid.CreateVersion7():N}@test.local", DisplayName = "Member",
            EmailVerified = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Set<User>().Add(user);
        db.Set<TenantMembership>().Add(new TenantMembership
        {
            TenantId = tenantId, UserId = user.Id, Role = TenantRoles.Member, JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return user.Id;
    }
}
