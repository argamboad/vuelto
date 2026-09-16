using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Income;

/// <summary>
/// INCOME-1 tenant-data hook: the household's income lines and every month's income rows count as data, are wiped on
/// dissolve (rows first; the month cascade would remove them anyway), and export together.
/// </summary>
public sealed class IncomeDataContributor(IRepository<IncomeLine> lines, IRepository<MonthIncome> rows) : ITenantDataContributor
{
    public string ExportKey => "income";

    public async Task<bool> HasDataAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await lines.QueryAllTenants().AnyAsync(l => l.TenantId == tenantId, cancellationToken)
        || await rows.QueryAllTenants().AnyAsync(r => r.TenantId == tenantId, cancellationToken);

    public async Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        // Query(): the dissolve has entered the target tenant, so the filter scopes these deletes to it.
        await rows.Query().Where(r => r.TenantId == tenantId).ExecuteDeleteAsync(cancellationToken);
        await lines.Query().Where(l => l.TenantId == tenantId).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<object?> ExportAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var lineRows = await lines.QueryAllTenants().Where(l => l.TenantId == tenantId)
            .OrderBy(l => l.SortOrder)
            .Select(l => new { l.Id, l.Name, l.MemberUserId, l.Currency, l.Kind, l.PayPeriod, l.Amount, l.PayDay1, l.PayDay2, l.IsActive, l.SortOrder, l.CreatedAt, l.UpdatedAt })
            .ToListAsync(cancellationToken);
        var monthRows = await rows.QueryAllTenants().Where(r => r.TenantId == tenantId)
            .OrderBy(r => r.MonthId).ThenBy(r => r.SortOrder)
            .Select(r => new { r.Id, r.MonthId, r.IncomeLineId, r.Label, r.MemberUserId, r.Currency, r.Amount, r.PlannedAmount, r.SortOrder, r.CreatedAt, r.UpdatedAt })
            .ToListAsync(cancellationToken);
        return lineRows.Count == 0 && monthRows.Count == 0 ? null : new { lines = lineRows, month_rows = monthRows };
    }
}
