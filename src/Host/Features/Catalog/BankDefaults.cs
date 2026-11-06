using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vuelto.Host.Budget;
using Vuelto.Host.Entities;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Vuelto.Host.Features.Catalog;

/// <summary>The Catalog's implementation of <see cref="IBankDefaults"/>: the slice that owns banks seeds them (Arch A8).</summary>
public sealed class BankDefaults(IRepository<Bank> banks, TimeProvider clock, ILogger<BankDefaults> logger) : IBankDefaults
{
    public async Task EnsureSeededAsync(Guid householdId, string? locale, CancellationToken cancellationToken = default)
    {
        if (await banks.Query().AnyAsync(cancellationToken)) return;
        var now = clock.GetUtcNow();
        foreach (var name in SeedCatalog.BankNames(locale))
            await banks.AddAsync(new Bank { TenantId = householdId, Name = name, CreatedAt = now, UpdatedAt = now }, cancellationToken);
        try
        {
            await banks.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent poll/sync seeded first: absorb the unique-name race; the caller reads what exists.
            logger.LogInformation(ex, "Bank seed for household {Id} lost a concurrent race; using the existing catalog", householdId);
        }
    }
}
