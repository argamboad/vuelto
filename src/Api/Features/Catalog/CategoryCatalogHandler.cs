using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Catalog;

/// <summary>Categories: seeded from <see cref="SeedCatalog.Categories"/>; error codes <c>category_exists*</c>.</summary>
public sealed class CategoryCatalogHandler(IRepository<Category> categories, ICurrentTenant tenant, TimeProvider clock)
    : CatalogHandler<Category>(categories, tenant, clock)
{
    protected override string Kind => "category";

    protected override IReadOnlyList<string> SeedNames(string? locale) => SeedCatalog.CategoryNames(locale);

    protected override Category NewEntry(Guid tenantId, string name, DateTimeOffset now) =>
        new() { TenantId = tenantId, Name = name, IsActive = true, CreatedAt = now, UpdatedAt = now };
}
