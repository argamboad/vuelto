namespace Vuelto.Core.Budget;

/// <summary>
/// The Catalog slice's face for seeding a household's banks from another slice (Arch A8, R162 — one slice writes an
/// entity; others read it or go through a Core contract the owner implements). The mail poll needs a bank to file a
/// voucher under before the household has visited the catalog; this seeds the defaults (<see cref="SeedCatalog.BankNames"/>)
/// when the household has none, and absorbs a concurrent seed. The current household is the one entered by the caller.
/// </summary>
public interface IBankDefaults
{
    Task EnsureSeededAsync(Guid householdId, string? locale, CancellationToken cancellationToken = default);
}
