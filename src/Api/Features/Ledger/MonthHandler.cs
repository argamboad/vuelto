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
/// <see cref="BudgetSettings"/> (or the defaults) at creation and are stored; the month's income rows are snapshotted
/// from the household's active <see cref="IncomeLine"/>s by their pay periods (INCOME-1, <see cref="IncomeSnapshot"/>)
/// and stay editable.
/// </summary>
public sealed class MonthHandler(
    IRepository<Month> months,
    IRepository<Week> weeks,
    IRepository<Transaction> transactions,
    IRepository<BudgetSettings> settings,
    IRepository<IncomeLine> incomeLines,
    IRepository<MonthIncome> monthIncomes,
    ITenantRepository tenants,
    IWeekBoundaryService boundaries,
    ICurrentTenant tenant,
    TimeProvider clock)
{
    /// <summary>Income rows staged with a new month in this request, so <see cref="Unstage"/> can undo them too.</summary>
    private readonly Dictionary<Guid, IReadOnlyList<MonthIncome>> _stagedIncome = [];

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
        var rows = await monthIncomes.Query().Where(r => r.MonthId == id).ToListAsync(cancellationToken);
        return MonthResponse.From(month, monthWeeks, rows);
    }

    /// <summary>Which month a date belongs to — the existing one, or the one that would be auto-created (<c>is_new</c>). Never writes.</summary>
    public async Task<MonthResolveResponse?> ResolveAsync(DateOnly date, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is null) return null;
        if (await FindContainingAsync(date, cancellationToken) is { } existing)
        {
            var week = await weeks.Query()
                .Where(w => w.MonthId == existing.Id && w.StartDate <= date && date <= w.EndDate)
                .Select(w => (int?)w.WeekNumber)
                .FirstOrDefaultAsync(cancellationToken);
            return new MonthResolveResponse(existing.Id, existing.Year, existing.MonthNumber, IsNew: false, WeekNumber: week);
        }

        var s = await SettingsAsync(cancellationToken);
        var (year, monthNumber) = boundaries.GetBudgetMonthForDate(date, s.WeekStartWeekday, s.MonthAnchor);
        // The week a new month would put the date in, from the same boundaries GetOrCreateForDateAsync uses to build it.
        var prospective = boundaries.GenerateWeeks(year, monthNumber, s.WeekStartWeekday, s.MonthAnchor)
            .FirstOrDefault(w => w.StartDate <= date && date <= w.EndDate)?.WeekNumber;
        return new MonthResolveResponse(null, year, monthNumber, IsNew: true, WeekNumber: prospective);
    }

    /// <summary>
    /// Replaces the month's income rows with <paramref name="request"/>'s list (INCOME-1): rows with an id are updated
    /// (their line link and plan kept), rows without one are one-offs, stored rows left out are removed — all in one save.
    /// </summary>
    public async Task<(MonthResponse? Month, ErrorResponse? Error)> UpdateIncomeAsync(Guid id, UpdateMonthIncomeRequest request, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId) return (null, new ErrorResponse("invalid_token", "No household on the token"));
        if (request.Rows is null) return (null, Invalid("rows is required"));

        var month = await months.Query().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (month is null) return (null, new ErrorResponse("not_found", "month not found"));

        var stored = await monthIncomes.Query().Where(r => r.MonthId == id).ToListAsync(cancellationToken);
        var members = await MemberIdsAsync(tenantId, cancellationToken);
        var seen = new HashSet<Guid>();
        foreach (var row in request.Rows)
        {
            if (string.IsNullOrWhiteSpace(row.Label)) return (null, Invalid("every row needs a label"));
            if (row.Label.Trim().Length > 100) return (null, Invalid("labels must be 100 characters or fewer"));
            if (Currencies.Normalize(row.Currency) is null) return (null, Invalid("row currencies must be CRC or USD"));
            if (row.Amount < 0) return (null, Invalid("income amounts cannot be negative"));
            if (row.MemberUserId is { } member && !members.Contains(member)) return (null, Invalid("member_user_id is not a member of this household"));
            if (row.Id is { } rowId && (!seen.Add(rowId) || stored.All(r => r.Id != rowId)))
                return (null, Invalid("a row id is repeated or does not belong to this month"));
        }

        var now = clock.GetUtcNow();
        var order = 0;
        foreach (var row in request.Rows)
        {
            var target = row.Id is { } rowId ? stored.Single(r => r.Id == rowId) : null;
            if (target is null)
            {
                target = new MonthIncome { TenantId = tenantId, MonthId = id, Label = "", CreatedAt = now };
                await monthIncomes.AddAsync(target, cancellationToken);
            }
            else monthIncomes.Update(target);
            target.Label = row.Label!.Trim();
            target.MemberUserId = row.MemberUserId;
            target.Currency = Currencies.Normalize(row.Currency)!;
            target.Amount = CurrencyMath.Round2(row.Amount);
            target.SortOrder = order++;
            target.UpdatedAt = now;
        }
        foreach (var gone in stored.Where(r => !seen.Contains(r.Id))) monthIncomes.Remove(gone);
        month.UpdatedAt = now;
        months.Update(month);
        await months.SaveChangesAsync(cancellationToken); // rows and month land together

        var rows = await monthIncomes.Query().Where(r => r.MonthId == id).ToListAsync(cancellationToken);
        return (MonthResponse.From(month, incomeRows: rows), null);
    }

    private static ErrorResponse Invalid(string message) => new("invalid_request", message);

    /// <summary>The household's current members — a line or row may only name one of them.</summary>
    private async Task<HashSet<Guid>> MemberIdsAsync(Guid tenantId, CancellationToken cancellationToken) =>
        (await tenants.GetMembersAsync(tenantId, cancellationToken)).Select(m => m.UserId).ToHashSet();

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
        var now = clock.GetUtcNow();

        var month = new Month
        {
            TenantId = tenantId,
            Year = year,
            MonthNumber = monthNumber,
            WeekCount = bounds.Count,
            Week1StartDate = bounds[0].StartDate,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var staged = bounds.Select(b => new Week
        {
            TenantId = tenantId, MonthId = month.Id, WeekNumber = b.WeekNumber, StartDate = b.StartDate, EndDate = b.EndDate,
        }).ToList();

        // The month's income plan: the active lines by their pay periods, skipping a member who has left (INCOME-1).
        var lines = await incomeLines.Query().Where(l => l.IsActive).ToListAsync(cancellationToken);
        var income = IncomeSnapshot.Rows(lines, tenantId, month.Id, bounds, await MemberIdsAsync(tenantId, cancellationToken), now);

        await months.AddAsync(month, cancellationToken);
        foreach (var w in staged) await weeks.AddAsync(w, cancellationToken);
        foreach (var r in income) await monthIncomes.AddAsync(r, cancellationToken);
        _stagedIncome[month.Id] = income;
        return (month, staged);
    }

    /// <summary>Undo <see cref="GetOrCreateForDateAsync"/>'s staging after a failed save (Added → Detached), so a retry starts clean.</summary>
    public void Unstage(Month month, IReadOnlyList<Week> staged)
    {
        if (staged.Count == 0) return; // the month pre-existed; nothing was staged
        if (_stagedIncome.Remove(month.Id, out var income))
            foreach (var r in income) monthIncomes.Remove(r);
        foreach (var w in staged) weeks.Remove(w);
        months.Remove(month);
    }

    /// <summary>
    /// Stages the removal of a month and its weeks when no transaction other than the
    /// <paramref name="leaving"/> ones remains in it (ADR-V005: months exist only through
    /// transactions). Returns whether the month was marked for removal. Not saved here.
    /// </summary>
    public async Task<bool> RemoveIfEmptyAsync(Guid monthId, IReadOnlyCollection<Guid> leaving, CancellationToken cancellationToken)
    {
        if (await transactions.Query().AnyAsync(t => t.MonthId == monthId && !leaving.Contains(t.Id), cancellationToken))
            return false;
        var month = await months.Query().FirstOrDefaultAsync(m => m.Id == monthId, cancellationToken);
        if (month is null) return false;
        foreach (var w in await weeks.Query().Where(w => w.MonthId == monthId).ToListAsync(cancellationToken)) weeks.Remove(w);
        foreach (var r in await monthIncomes.Query().Where(r => r.MonthId == monthId).ToListAsync(cancellationToken)) monthIncomes.Remove(r);
        months.Remove(month);
        return true;
    }

    /// <summary>The household's saved settings, or the defaults it runs on until it saves (BUDGET-1).</summary>
    private async Task<BudgetSettings> SettingsAsync(CancellationToken cancellationToken) =>
        await settings.Query().FirstOrDefaultAsync(cancellationToken) ?? BudgetSettings.Defaults(tenant.TenantId ?? Guid.Empty);
}
