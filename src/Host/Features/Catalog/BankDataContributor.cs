using Vuelto.Host.Entities;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Vuelto.Host.Features.Catalog;

public sealed class BankDataContributor(IRepository<Bank> banks) : CatalogDataContributor<Bank>(banks)
{
    public override string ExportKey => "banks";
}
