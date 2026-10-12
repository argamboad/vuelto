using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Catalog;

/// <summary>Banks: seeded from <see cref="SeedCatalog.Banks"/> (Cash first); error codes <c>bank_exists*</c>.</summary>
public sealed class BankCatalogHandler(IRepository<Bank> banks, ICurrentTenant tenant, TimeProvider clock)
    : CatalogHandler<Bank>(banks, tenant, clock)
{
    protected override string Kind => "bank";

    protected override IReadOnlyList<string> SeedNames(string? locale) => SeedCatalog.BankNames(locale);

    protected override Bank NewEntry(Guid tenantId, string name, DateTimeOffset now) =>
        new() { TenantId = tenantId, Name = name, IsActive = true, CreatedAt = now, UpdatedAt = now };
}
