using Microsoft.EntityFrameworkCore;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Perezosoft.Api.Features.Notes;

/// <summary>
/// 🗑️ DELETE-ME: sample feature's tenant-data hook. Registering this (in Program.cs) is
/// all it takes for the dissolve flow to account for and wipe this feature's data — no
/// edits to any central wipe method. Every real tenant-scoped feature ships one of these.
/// </summary>
public class NotesDataContributor(IRepository<Note> notes) : ITenantDataContributor
{
    public Task<bool> HasDataAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        // QueryAllTenants: dissolve runs for a tenant other than the current one, so this
        // crosses tenants by design — the audited escape hatch, re-constrained to the target.
        notes.QueryAllTenants().AnyAsync(n => n.TenantId == tenantId, cancellationToken);

    public async Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        // Query() (not QueryAllTenants): the dissolve enters the target tenant (RLS-2/T6), so the filter
        // scopes this to it; composing QueryAllTenants() with a set-based write is banned (RLS-4/T7).
        await notes.Query()
            .Where(n => n.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);

    public string ExportKey => "notes";

    public async Task<object?> ExportAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await notes.QueryAllTenants()
            .Where(n => n.TenantId == tenantId)
            .OrderBy(n => n.CreatedAt)
            .Select(n => new { n.Id, n.Title, n.Content, n.CreatedAt, n.UpdatedAt })
            .ToListAsync(cancellationToken);
}
