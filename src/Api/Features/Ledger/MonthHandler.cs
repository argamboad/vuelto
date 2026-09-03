using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Ledger;

/// <summary>
/// LEDGER-1: the household's budget months (ADR-V005). Months are never created by a request of their
/// own — <see cref="GetOrCreateForDateAsync"/> is called by the transaction path and only <em>stages</em>
/// the new month and its weeks on the shared context, so the caller's single <c>SaveChanges</c> lands
/// month, weeks and transaction atomically (or nothing). Boundaries come from the household's
/// <see cref="BudgetSettings"/> (or the defaults) at creation and are stored; income is snapshotted
/// from the matching 4-/5-week default and stays editable.
/// </summary>
public sealed class MonthHandler(
    IRepository<Month> months,
    IRepository<Week> weeks,
    IRepository<Transaction> transactions,
    IRepository<BudgetSettings> settings,
    IWeekBoundaryService boundaries,
    ICurrentTenant tenant,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<MonthResponse>?> ListAsync(CancellationToken cancellationToken)
    {
        if (tenant.TenantId is null) return null;
        var rows = await months.Query()
            .OrderByDescending(m => m.Year).ThenByDescending(m => m.MonthNumber)
            .ToListAsync(cancellationToken);
        return rows.Select(m => MonthResponse.From(m)).ToList();
    }

    /// <summary>Null = not found for this household (uniform 404).</summary>
    public async Task<MonthResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var month = await months.Query().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (month is null) return null;
        var monthWeeks = await weeks.Query().Where(w => w.MonthId == id).OrderBy(w => w.WeekNumber).ToListAsync(cancellationToken);
        return MonthResponse.From(month, monthWeeks);
    }

    /// <summary>Which month a date belongs to — the existing one, or the one that would be auto-created (<c>is_new</c>). Never writes.</summary>
    public async Task<MonthResolveResponse?> ResolveAsync(DateOnly date, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is null) return null;
        if (await FindContainingAsync(date, cancellationToken) is { } existing)
            return new MonthResolveResponse(existing.Id, existing.Year, existing.MonthNumber, IsNew: false);

        var s = await SettingsAsync(cancellationToken);
        var (year, monthNumber) = boundaries.GetBudgetMonthForDate(date, s.WeekStartWeekday, s.MonthAnchor);
        return new MonthResolveResponse(null, year, monthNumber, IsNew: true);
    }

    public async Task<(MonthResponse? Month, ErrorResponse? Error)> UpdateIncomeAsync(Guid id, UpdateMonthIncomeRequest request, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is null) return (null, new ErrorResponse("invalid_token", "No household on the token"));
        if (request.PrimaryIncomeAmount < 0 || request.SecondaryIncomeAmount < 0)
            return (null, new ErrorResponse("invalid_request", "income amounts cannot be negative"));
        var primary = Currencies.Normalize(request.PrimaryIncomeCurrency);
        var secondary = Currencies.Normalize(request.SecondaryIncomeCurrency);
        if (primary is null || secondary is null)
            return (null, new ErrorResponse("invalid_request", "income currencies must be CRC or USD"));

        var month = await months.Query().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (month is null) return (null, new ErrorResponse("not_found", "month not found"));

        month.PrimaryIncomeAmount = CurrencyMath.Round2(request.PrimaryIncomeAmount);
        month.PrimaryIncomeCurrency = primary;
        month.SecondaryIncomeAmount = CurrencyMath.Round2(request.SecondaryIncomeAmount);
        month.SecondaryIncomeCurrency = secondary;
        month.UpdatedAt = clock.GetUtcNow();
        months.Update(month);
        await months.SaveChangesAsync(cancellationToken);
        return (MonthResponse.From(month), null);
    }

    /// <summary>The month whose stored window contains the date, or null.</summary>
    public async Task<Month?> FindContainingAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var candidate = await months.Query()
            .Where(m => m.Week1StartDate <= date)
            .OrderByDescending(m => m.Week1StartDate)
            .FirstOrDefaultAsync(cancellationToken);
        if (candidate is null) return null;

        var lastWeekEnd = await weeks.Query().Where(w => w.MonthId == candidate.Id).MaxAsync(w => (DateOnly?)w.EndDate, cancellationToken);
        return lastWeekEnd >= date ? candidate : null;
    }

    /// <summary>
    /// The month covering the date, staging a new month + weeks on the context when none does (not
    /// saved here — the caller's <c>SaveChanges</c> commits everything together). <c>Staged</c> tells the
    /// caller which entities to detach if that save loses a concurrent-creation race.
    /// </summary>
    public async Task<(Month Month, IReadOnlyList<Week> Staged)> GetOrCreateForDateAsync(Guid tenantId, DateOnly date, CancellationToken cancellationToken)
    {
        if (await FindContainingAsync(date, cancellationToken) is { } existing)
            return (existing, []);

        var s = await SettingsAsync(cancellationToken);
        var (year, monthNumber) = boundaries.GetBudgetMonthForDate(date, s.WeekStartWeekday, s.MonthAnchor);
        var bounds = boundaries.GenerateWeeks(year, monthNumber, s.WeekStartWeekday, s.MonthAnchor);
        var fiveWeeks = bounds.Count == 5;
        var now = clock.GetUtcNow();

        var month = new Month
        {
            TenantId = tenantId,
            Year = year,
            MonthNumber = monthNumber,
            WeekCount = bounds.Count,
            Week1StartDate = bounds[0].StartDate,
            PrimaryIncomeAmount = fiveWeeks ? s.PrimaryIncome5w : s.PrimaryIncome4w,
            PrimaryIncomeCurrency = s.PrimaryIncomeCurrency,
            SecondaryIncomeAmount = fiveWeeks ? s.SecondaryIncome5w : s.SecondaryIncome4w,
            SecondaryIncomeCurrency = s.SecondaryIncomeCurrency,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var staged = bounds.Select(b => new Week
        {
            TenantId = tenantId, MonthId = month.Id, WeekNumber = b.WeekNumber, StartDate = b.StartDate, EndDate = b.EndDate,
        }).ToList();

        await months.AddAsync(month, cancellationToken);
        foreach (var w in staged) await weeks.AddAsync(w, cancellationToken);
        return (month, staged);
    }

    /// <summary>Undo <see cref="GetOrCreateForDateAsync"/>'s staging after a failed save (Added → Detached), so a retry starts clean.</summary>
    public void Unstage(Month month, IReadOnlyList<Week> staged)
    {
        if (staged.Count == 0) return; // the month pre-existed; nothing was staged
        foreach (var w in staged) weeks.Remove(w);
        months.Remove(month);
    }

    /// <summary>
    /// Stages the removal of a month and its weeks when no transaction other than
    /// <paramref name="leavingTransactionId"/> remains in it (ADR-V005: months exist only through
    /// transactions). Returns whether the month was marked for removal. Not saved here.
    /// </summary>
    public async Task<bool> RemoveIfEmptyAsync(Guid monthId, Guid leavingTransactionId, CancellationToken cancellationToken)
    {
        if (await transactions.Query().AnyAsync(t => t.MonthId == monthId && t.Id != leavingTransactionId, cancellationToken))
            return false;
        var month = await months.Query().FirstOrDefaultAsync(m => m.Id == monthId, cancellationToken);
        if (month is null) return false;
        foreach (var w in await weeks.Query().Where(w => w.MonthId == monthId).ToListAsync(cancellationToken)) weeks.Remove(w);
        months.Remove(month);
        return true;
    }

    /// <summary>The household's saved settings, or the defaults it runs on until it saves (BUDGET-1).</summary>
    private async Task<BudgetSettings> SettingsAsync(CancellationToken cancellationToken) =>
        await settings.Query().FirstOrDefaultAsync(cancellationToken) ?? BudgetSettings.Defaults(tenant.TenantId ?? Guid.Empty);
}
