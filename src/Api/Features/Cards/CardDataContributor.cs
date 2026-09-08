using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Cards;

/// <summary>CARDS-1: the household's cards are its data — wiped on dissolve, exported as a list.</summary>
public sealed class CardDataContributor(IRepository<Card> cards, IRepository<CardIdentity> identities) : ITenantDataContributor
{
    public string ExportKey => "cards";

    public Task<bool> HasDataAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        cards.QueryAllTenants().AnyAsync(c => c.TenantId == tenantId, cancellationToken);

    public async Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await identities.Query().Where(i => i.TenantId == tenantId).ExecuteDeleteAsync(cancellationToken);
        await cards.Query().Where(c => c.TenantId == tenantId).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<object?> ExportAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var known = await identities.QueryAllTenants().Where(i => i.TenantId == tenantId).OrderBy(i => i.CreatedAt).ToListAsync(cancellationToken);
        var rows = (await cards.QueryAllTenants()
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken))
            .Select(c => new { c.Id, c.Name, c.Brand, c.Last4, c.BankId, c.IsActive, c.AutoNamed, c.CreatedAt, c.UpdatedAt,
                Identities = known.Where(i => i.CardId == c.Id).Select(i => new { i.Brand, i.Last4 }).ToList() })
            .ToList();
        return rows.Count == 0 ? null : rows;
    }
}
