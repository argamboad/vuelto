using Vuelto.Host.Entities;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Vuelto.Host.Features.Catalog;

public sealed class CategoryDataContributor(IRepository<Category> categories) : CatalogDataContributor<Category>(categories)
{
    public override string ExportKey => "categories";
}
