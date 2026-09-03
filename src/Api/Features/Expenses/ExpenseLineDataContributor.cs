using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Expenses;

/// <summary>Tenant-data hook shared by both budget-line tables: the household's lines count as data, are wiped on dissolve, and export in list order.</summary>
public abstract class ExpenseLineDataContributor<TLine>(IRepository<TLine> lines) : ITenantDataContributor
    where TLine : class, IExpenseLine
{
    /// <summary>A constant per list — the platform's export-key gate reads it from an uninitialized instance.</summary>
    public abstract string ExportKey { get; }

    public Task<bool> HasDataAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        lines.QueryAllTenants().AnyAsync(e => e.TenantId == tenantId, cancellationToken);

    public async Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await lines.Query().Where(e => e.TenantId == tenantId).ExecuteDeleteAsync(cancellationToken);

    public async Task<object?> ExportAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var rows = await lines.QueryAllTenants().Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.SortOrder)
            .Select(e => new { e.Id, e.Name, e.BudgetCrc, e.BudgetUsd, e.PaymentMethod, e.CategoryId, e.BankId, e.SortOrder, e.IsActive, e.CreatedAt, e.UpdatedAt })
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : rows;
    }
}

public sealed class FixedExpenseDataContributor(IRepository<FixedExpense> lines) : ExpenseLineDataContributor<FixedExpense>(lines)
{
    public override string ExportKey => "fixed_expenses";
}

public sealed class VariableExpenseDataContributor(IRepository<VariableExpense> lines) : ExpenseLineDataContributor<VariableExpense>(lines)
{
    public override string ExportKey => "variable_expenses";
}
