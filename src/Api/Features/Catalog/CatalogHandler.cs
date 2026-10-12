using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Catalog;

/// <summary>The non-generic face of <see cref="CatalogHandler{TEntry}"/> the endpoints bind to.</summary>
public interface ICatalogHandler
{
    Task<IReadOnlyList<CatalogEntryResponse>?> ListAsync(bool includeInactive, string? locale, CancellationToken cancellationToken);
    Task<(CatalogEntryResponse? Entry, ErrorResponse? Error)> CreateAsync(CreateCatalogEntryRequest request, CancellationToken cancellationToken);
    Task<(CatalogEntryResponse? Entry, ErrorResponse? Error)> UpdateAsync(Guid id, UpdateCatalogEntryRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The one catalog behaviour, generic over the entry type (ADR-V008): list (seeding the household's
/// defaults on its very first read, in the caller's locale — ADR-V009), create with the 409
/// reactivation offer, and update (rename / activate / deactivate). <c>Query()</c> is tenant-filtered
/// by the platform, so another household's entries are simply not found (ADR-V008: uniform 404).
/// </summary>
public abstract class CatalogHandler<TEntry>(IRepository<TEntry> entries, ICurrentTenant tenant, TimeProvider clock) : ICatalogHandler
    where TEntry : class, ICatalogEntry
{
    /// <summary>The error-code prefix (<c>category</c> / <c>bank</c>) — codes are <c>{kind}_exists</c> and <c>{kind}_exists_inactive</c>.</summary>
    protected abstract string Kind { get; }

    /// <summary>The default names to seed for a new household, in the given locale.</summary>
    protected abstract IReadOnlyList<string> SeedNames(string? locale);

    protected abstract TEntry NewEntry(Guid tenantId, string name, DateTimeOffset now);

    public async Task<IReadOnlyList<CatalogEntryResponse>?> ListAsync(bool includeInactive, string? locale, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId) return null;

        if (!await entries.Query().AnyAsync(cancellationToken))
            await SeedAsync(tenantId, locale, cancellationToken);

        var query = includeInactive ? entries.Query() : entries.Query().Where(e => e.IsActive);
        var rows = await query.OrderBy(e => e.Name).ToListAsync(cancellationToken);
        return rows.Select(CatalogEntryResponse.From).ToList();
    }

    public async Task<(CatalogEntryResponse? Entry, ErrorResponse? Error)> CreateAsync(CreateCatalogEntryRequest request, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId) return (null, NoTenant());
        if (string.IsNullOrWhiteSpace(request.Name)) return (null, new ErrorResponse("invalid_request", "name is required"));

        var name = request.Name.Trim();
        if (await FindByNameAsync(name, cancellationToken) is { } existing)
            return (null, existing.IsActive
                ? new CatalogConflictResponse($"{Kind}_exists", $"A {Kind} named '{existing.Name}' already exists", null, null)
                : new CatalogConflictResponse($"{Kind}_exists_inactive", $"'{existing.Name}' already exists but is inactive — reactivate it?", existing.Id, existing.Name));

        var entry = NewEntry(tenantId, name, clock.GetUtcNow());
        await entries.AddAsync(entry, cancellationToken);
        await entries.SaveChangesAsync(cancellationToken);
        return (CatalogEntryResponse.From(entry), null);
    }

    public async Task<(CatalogEntryResponse? Entry, ErrorResponse? Error)> UpdateAsync(Guid id, UpdateCatalogEntryRequest request, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is null) return (null, NoTenant());
        if (string.IsNullOrWhiteSpace(request.Name)) return (null, new ErrorResponse("invalid_request", "name is required"));

        // Tenant-scoped lookup: another household's id is not found, never 403 (no existence oracle).
        var entry = await entries.Query().FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (entry is null) return (null, new ErrorResponse("not_found", $"{Kind} not found"));

        var name = request.Name.Trim();
        if (await FindByNameAsync(name, cancellationToken) is { } clash && clash.Id != id)
            return (null, new CatalogConflictResponse($"{Kind}_exists", $"A {Kind} named '{clash.Name}' already exists", null, null));

        entry.Name = name;
        entry.IsActive = request.IsActive;
        entry.UpdatedAt = clock.GetUtcNow();
        entries.Update(entry);
        await entries.SaveChangesAsync(cancellationToken);
        return (CatalogEntryResponse.From(entry), null);
    }

    /// <summary>Case-insensitive name match within the household (Postgres <c>lower()</c> on both sides).</summary>
    private Task<TEntry?> FindByNameAsync(string name, CancellationToken cancellationToken)
    {
        var lowered = name.ToLowerInvariant();
        return entries.Query().FirstOrDefaultAsync(e => e.Name.ToLower() == lowered, cancellationToken);
    }

    /// <summary>
    /// First-read seeding. One row per SaveChanges in a stable order, so two concurrent first reads take
    /// the unique (TenantId, Name) locks in the same order and the loser absorbs a clean per-row unique
    /// violation — the default set is never duplicated and never partially blocks (donor A10).
    /// </summary>
    private async Task SeedAsync(Guid tenantId, string? locale, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        foreach (var name in SeedNames(locale).OrderBy(n => n, StringComparer.Ordinal))
        {
            var entry = NewEntry(tenantId, name, now);
            await entries.AddAsync(entry, cancellationToken);
            try
            {
                await entries.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                entries.Remove(entry); // Added → Detached: the other seeder won this row
            }
        }
    }

    private static ErrorResponse NoTenant() => new("invalid_token", "No household on the token");
}
