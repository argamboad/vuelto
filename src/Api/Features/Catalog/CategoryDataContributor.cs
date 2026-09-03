using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Catalog;

public sealed class CategoryDataContributor(IRepository<Category> categories) : CatalogDataContributor<Category>(categories)
{
    public override string ExportKey => "categories";
}
