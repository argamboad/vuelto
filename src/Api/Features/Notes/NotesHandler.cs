using Microsoft.EntityFrameworkCore;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Perezosoft.Api.Features.Notes;

/// <summary>
/// 🗑️ DELETE-ME: sample feature handler — the only place this feature's logic lives.
/// <para>
/// Note how little it needs: the generic <see cref="IRepository{T}"/> (whose
/// <c>Query()</c> is already tenant-filtered, so a read can't leak across tenants) and
/// <see cref="ICurrentTenant"/> for the owning tenant id. No bespoke repository, no edits
/// to platform code. This is the shape every real feature slice copies.
/// </para>
/// </summary>
public class NotesHandler(IRepository<Note> notes, ICurrentTenant tenant, TimeProvider clock)
{
    public async Task<IReadOnlyList<NoteResponse>> ListAsync(CancellationToken cancellationToken)
    {
        // Query() is tenant-scoped by the global filter — no explicit Where(TenantId) needed.
        var rows = await notes.Query()
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync(cancellationToken);
        return rows.Select(NoteResponse.From).ToList();
    }

    public async Task<NoteResponse?> CreateAsync(CreateNoteRequest request, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId) return null; // no tenant on the token
        if (string.IsNullOrWhiteSpace(request.Title)) return null; // endpoint maps null → 400

        var now = clock.GetUtcNow();
        var note = new Note
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Title = request.Title.Trim(),
            Content = request.Content?.Trim() ?? string.Empty,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await notes.AddAsync(note, cancellationToken);
        await notes.SaveChangesAsync(cancellationToken);
        return NoteResponse.From(note);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        // Tenant-scoped lookup: a note from another tenant simply isn't found here.
        var note = await notes.Query().FirstOrDefaultAsync(n => n.Id == id, cancellationToken);
        if (note is null) return false;

        notes.Remove(note);
        await notes.SaveChangesAsync(cancellationToken);
        return true;
    }
}
