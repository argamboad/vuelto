using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Catalog;

public sealed class BankDataContributor(IRepository<Bank> banks) : CatalogDataContributor<Bank>(banks)
{
    public override string ExportKey => "banks";
}
