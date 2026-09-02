using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Budget;

/// <summary>
/// BUDGET-1's tenant-data hook: the settings row counts as household data (a solo owner with saved
/// settings is asked before abandoning it), is wiped on dissolve, and is exported under
/// <c>budget_settings</c>. Same shape every budget slice follows.
/// </summary>
public class BudgetSettingsDataContributor(IRepository<BudgetSettings> settings) : ITenantDataContributor
{
    public Task<bool> HasDataAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        // Cross-tenant by design: dissolve/accept run for a tenant other than the current one.
        settings.QueryAllTenants().AnyAsync(s => s.TenantId == tenantId, cancellationToken);

    public async Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        // Query(): the dissolve has entered the target tenant, so the filter scopes this delete to it
        // (composing QueryAllTenants with a set-based write is banned).
        await settings.Query()
            .Where(s => s.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);

    public string ExportKey => "budget_settings";

    public async Task<object?> ExportAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await settings.QueryAllTenants()
            .Where(s => s.TenantId == tenantId)
            .Select(s => new
            {
                s.WeekStartWeekday, s.MonthAnchor,
                s.PrimaryIncome4w, s.PrimaryIncome5w, s.PrimaryIncomeCurrency,
                s.SecondaryIncome4w, s.SecondaryIncome5w, s.SecondaryIncomeCurrency,
                s.CreatedAt, s.UpdatedAt,
            })
            .FirstOrDefaultAsync(cancellationToken);
}
