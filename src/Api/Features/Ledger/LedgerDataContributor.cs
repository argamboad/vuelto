using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Ledger;

/// <summary>
/// Tenant-data hook for the ledger: months, weeks and transactions count as data, are wiped on
/// dissolve (transactions → weeks → months, honouring the FKs), and export as months with their weeks
/// plus the flat transaction list.
/// </summary>
public sealed class LedgerDataContributor(IRepository<Month> months, IRepository<Week> weeks, IRepository<Transaction> transactions) : ITenantDataContributor
{
    /// <summary>A constant — the platform's export-key gate reads it from an uninitialized instance.</summary>
    public string ExportKey => "ledger";

    public async Task<bool> HasDataAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await months.QueryAllTenants().AnyAsync(m => m.TenantId == tenantId, cancellationToken)
        || await transactions.QueryAllTenants().AnyAsync(t => t.TenantId == tenantId, cancellationToken);

    public async Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await transactions.Query().Where(t => t.TenantId == tenantId).ExecuteDeleteAsync(cancellationToken);
        await weeks.Query().Where(w => w.TenantId == tenantId).ExecuteDeleteAsync(cancellationToken);
        await months.Query().Where(m => m.TenantId == tenantId).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<object?> ExportAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var monthRows = await months.QueryAllTenants().Where(m => m.TenantId == tenantId)
            .OrderBy(m => m.Year).ThenBy(m => m.MonthNumber).ToListAsync(cancellationToken);
        if (monthRows.Count == 0) return null;

        var weekRows = await weeks.QueryAllTenants().Where(w => w.TenantId == tenantId).OrderBy(w => w.WeekNumber).ToListAsync(cancellationToken);
        var txRows = await transactions.QueryAllTenants().Where(t => t.TenantId == tenantId)
            .OrderBy(t => t.TransactionDate).ThenBy(t => t.CreatedAt)
            .Select(t => new { t.Id, t.MonthId, t.Payee, t.BankId, t.CategoryId, t.EnvelopeId, t.PaymentMethod, t.OriginalAmount, t.Currency, t.TransactionDate, t.AmountCrc, t.AmountUsd, t.ExchangeRateUsed, t.TransactionType, t.Source, t.CreatedAt, t.UpdatedAt })
            .ToListAsync(cancellationToken);

        return new
        {
            months = monthRows.Select(m => new
            {
                m.Id, m.Year, m.MonthNumber, m.WeekCount, m.Week1StartDate,
                m.PrimaryIncomeAmount, m.PrimaryIncomeCurrency, m.SecondaryIncomeAmount, m.SecondaryIncomeCurrency, m.CreatedAt, m.UpdatedAt,
                weeks = weekRows.Where(w => w.MonthId == m.Id).Select(w => new { w.WeekNumber, w.StartDate, w.EndDate }),
            }),
            transactions = txRows,
        };
    }
}
