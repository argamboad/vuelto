using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Income;

/// <summary>
/// INCOME-1 per-user hook (GDPR-2): a member's id on income lines and month rows is personal data, so account erasure
/// clears it in every household that recorded it — the amounts stay, they are the household's. Cross-tenant: the
/// households are found with the sanctioned read, then each update runs inside that household (a set-based write
/// carries no query tag, so it must enter the tenant — ADR-020).
/// </summary>
public sealed class IncomeUserDataContributor(
    IRepository<IncomeLine> lines, IRepository<MonthIncome> rows, ITenantContext tenantContext) : IUserDataContributor
{
    public async Task WipeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var households = (await lines.QueryAllTenants().Where(l => l.MemberUserId == userId).Select(l => l.TenantId).ToListAsync(cancellationToken))
            .Concat(await rows.QueryAllTenants().Where(r => r.MemberUserId == userId).Select(r => r.TenantId).ToListAsync(cancellationToken))
            .Distinct()
            .ToList();

        foreach (var household in households)
        {
            using (tenantContext.EnterTenant(household))
            {
                await lines.Query().Where(l => l.MemberUserId == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.MemberUserId, (Guid?)null), cancellationToken);
                await rows.Query().Where(r => r.MemberUserId == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.MemberUserId, (Guid?)null), cancellationToken);
            }
        }
    }
}
