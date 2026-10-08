using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Ledger;

/// <summary>The Ledger's implementation of <see cref="IMonthIncomeRows"/>: the slice that owns the month's rows writes them (Arch A8).</summary>
public sealed class MonthIncomeRows(IRepository<MonthIncome> rows) : IMonthIncomeRows
{
    public async Task ReassignMemberAsync(Guid incomeLineId, Guid? from, Guid? to, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var copied = await rows.Query()
            .Where(r => r.IncomeLineId == incomeLineId && r.MemberUserId == from)
            .ToListAsync(cancellationToken);
        foreach (var row in copied)
        {
            row.MemberUserId = to;
            row.UpdatedAt = now;
            rows.Update(row);
        }
    }

    public Task ClearMemberAsync(Guid userId, CancellationToken cancellationToken = default) =>
        rows.Query().Where(r => r.MemberUserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.MemberUserId, (Guid?)null), cancellationToken);

    public Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        rows.Query().Where(r => r.TenantId == tenantId).ExecuteDeleteAsync(cancellationToken);
}
