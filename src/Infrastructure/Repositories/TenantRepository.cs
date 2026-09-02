using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="ITenantRepository"/>.</summary>
public class TenantRepository(AppDbContext db) : ITenantRepository
{
    public async Task<Guid?> GetTenantIdForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var member = await db.TenantMemberships
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserId == userId, cancellationToken);
        return member?.TenantId;
    }

    public async Task<Tenant> CreateAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(cancellationToken);
        return tenant;
    }

    public async Task<TenantMembership> AddMemberAsync(TenantMembership member, CancellationToken cancellationToken = default)
    {
        db.TenantMemberships.Add(member);
        await db.SaveChangesAsync(cancellationToken);
        return member;
    }

    public async Task<Tenant?> GetByIdAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

    public async Task<TenantMembership?> GetMembershipAsync(Guid userId, CancellationToken cancellationToken = default) =>
        // Ordered so a user with >1 membership resolves deterministically (oldest), never arbitrarily (LB-ADM-3).
        await db.TenantMemberships
            .OrderBy(m => m.JoinedAt).ThenBy(m => m.TenantId)
            .FirstOrDefaultAsync(m => m.UserId == userId, cancellationToken);

    public async Task<TenantMembership?> GetMembershipAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken = default) =>
        // The (user, tenant) pair is unique, so this is inherently deterministic — the authz lookup (LB-ADM-3).
        await db.TenantMemberships.FirstOrDefaultAsync(m => m.UserId == userId && m.TenantId == tenantId, cancellationToken);

    public async Task<List<TenantMembership>> GetMembersAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await db.TenantMemberships.Where(m => m.TenantId == tenantId).ToListAsync(cancellationToken);

    public async Task<bool> IsEmailMemberAsync(Guid tenantId, string email, CancellationToken cancellationToken = default)
    {
        // Emails are stored normalized (ToLowerInvariant on write); normalize the input the
        // same way in C# and compare to the column directly — no per-row SQL ToLower() (which
        // is culture-dependent and defeats the index).
        var normalized = email.ToLowerInvariant();
        return await (from m in db.TenantMemberships
                      join u in db.Users on m.UserId equals u.Id
                      where m.TenantId == tenantId && u.Email == normalized
                      select m.Id).AnyAsync(cancellationToken);
    }

    public async Task UpdateMemberAsync(TenantMembership member, CancellationToken cancellationToken = default)
    {
        db.TenantMemberships.Update(member);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> TryTransferOwnershipAsync(Guid tenantId, Guid currentOwnerUserId, Guid targetUserId, CancellationToken cancellationToken = default)
    {
        // Guard on the current owner's role first. If 0 rows are affected, someone else
        // already changed the owner (race lost).
        var affected = await db.TenantMemberships
            .Where(m => m.TenantId == tenantId && m.UserId == currentOwnerUserId && m.Role == TenantRoles.Owner)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, TenantRoles.Member), cancellationToken);
        if (affected == 0) return false;

        // Unconditional flip on the target — pre-validated by the service layer.
        await db.TenantMemberships
            .Where(m => m.TenantId == tenantId && m.UserId == targetUserId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, TenantRoles.Owner), cancellationToken);
        return true;
    }

    public async Task DeleteTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant == null) return;
        db.Tenants.Remove(tenant);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<TenantMemberDetail>> GetMemberDetailsAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await (from m in db.TenantMemberships
               join u in db.Users on m.UserId equals u.Id
               where m.TenantId == tenantId
               orderby u.Email
               select new TenantMemberDetail(m.UserId, u.DisplayName, u.Email, m.Role, m.JoinedAt))
            .ToListAsync(cancellationToken);

    public async Task<List<TenantSummary>> ListAllAsync(CancellationToken cancellationToken = default) =>
        await db.Tenants
            .OrderBy(t => t.Name)
            .Select(t => new TenantSummary(t.Id, t.Name, t.CreatedAt, db.TenantMemberships.Count(m => m.TenantId == t.Id)))
            .ToListAsync(cancellationToken);

    public async Task UpdateTenantAsync(Tenant tenant, CancellationToken cancellationToken = default)
    {
        db.Tenants.Update(tenant);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveMemberAsync(TenantMembership member, CancellationToken cancellationToken = default)
    {
        db.TenantMemberships.Remove(member);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task WipeDataAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        // Core teardown only. Feature/domain tables are wiped by their ITenantDataContributor
        // (called first, in the same transaction) — nothing to edit here per new feature.
        //
        // Target the ARGUMENT tenant explicitly and filter-independently: TenantInvitation is
        // ITenantScoped, so a plain Where() would also be narrowed by the global read filter and
        // miss the target's rows whenever a *different* tenant is current (CONF-2). IgnoreQueryFilters
        // makes the delete depend on the argument alone, not on who is calling. ExecuteDeleteAsync
        // enlists in the ambient transaction (db.Database.CurrentTransaction) the dissolve flow opens,
        // so the wipe stays all-or-nothing.
        // RLS (ADR-020): these deletes are UNTAGGED, so the DB policy scopes them to the CURRENT tenant
        // (unlike a QueryAllTenants() tag, which does render — and bypass — for ExecuteDelete). That is
        // safe because every dissolve path runs with the ARGUMENT tenant current: TenantDissolutionService
        // enters it (RLS-2 hardening), and TenantMemberships/Tenants aren't RLS'd anyway. Belt: the
        // Tenants FK cascades to invitations/memberships even if a delete here is filtered.
        await db.TenantInvitations.IgnoreQueryFilters()
            .Where(i => i.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.TenantMemberships.IgnoreQueryFilters()
            .Where(m => m.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.Tenants.IgnoreQueryFilters()
            .Where(t => t.Id == tenantId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
