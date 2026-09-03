using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Dashboard;

/// <summary>
/// DASH-1: gathers one month's inputs through tenant-filtered <c>Query()</c> (the month, its weeks and
/// transactions and refunds, the active envelopes, both budget-line lists, ALL categories and banks for
/// names) and hands them to the pure <see cref="IDashboardSummaryService"/> with the rate resolved
/// through the ADR-V006 chain. No rate → no summary, <c>rate_unavailable</c> set. Reads only.
/// </summary>
public sealed class DashboardHandler(
    IRepository<Month> months,
    IRepository<Week> weeks,
    IRepository<Transaction> transactions,
    IRepository<Refund> refunds,
    IRepository<Envelope> envelopes,
    IRepository<FixedExpense> fixedExpenses,
    IRepository<VariableExpense> variableExpenses,
    IRepository<Category> categories,
    IRepository<Bank> banks,
    IDashboardSummaryService summary,
    IExchangeRateResolver rates,
    ICurrentTenant tenant)
{
    /// <summary>Null = month not found for this household (uniform 404).</summary>
    public async Task<DashboardResponse?> GetAsync(Guid monthId, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is null) return null;
        var month = await months.Query().FirstOrDefaultAsync(m => m.Id == monthId, cancellationToken);
        if (month is null) return null;

        var monthWeeks = await weeks.Query().Where(w => w.MonthId == monthId).OrderBy(w => w.WeekNumber).ToListAsync(cancellationToken);
        var header = DashboardMonthResponse.From(month, monthWeeks);

        var resolved = await rates.ResolveAsync(cancellationToken);
        if (resolved is null)
            return new DashboardResponse(header, null, null, null, RateUnavailable: true, Summary: null);

        var monthTransactions = await transactions.Query().Where(t => t.MonthId == monthId).ToListAsync(cancellationToken);
        var monthRefunds = await refunds.Query().Where(r => r.MonthId == monthId).ToListAsync(cancellationToken);
        var allEnvelopes = await envelopes.Query().ToListAsync(cancellationToken);
        var fixedLines = await fixedExpenses.Query().ToListAsync(cancellationToken);
        var variableLines = await variableExpenses.Query().ToListAsync(cancellationToken);
        var categoryNames = await categories.Query().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken); // all states
        var bankNames = await banks.Query().ToDictionaryAsync(b => b.Id, b => b.Name, cancellationToken);         // all states

        var calc = summary.Calculate(month, monthWeeks, monthTransactions, fixedLines, variableLines, monthRefunds, allEnvelopes, resolved.Rate, categoryNames, bankNames);
        return new DashboardResponse(header, resolved.Rate, resolved.Source, resolved.AsOf, RateUnavailable: false, DashboardSummaryResponse.From(calc));
    }
}
