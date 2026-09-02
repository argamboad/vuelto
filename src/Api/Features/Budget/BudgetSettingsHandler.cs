using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Budget;

/// <summary>
/// BUDGET-1: read and upsert the household's single <see cref="BudgetSettings"/> row. A read never
/// writes — a household that has not saved yet gets <see cref="BudgetSettings.Defaults"/> flagged
/// <c>is_default</c>. <c>Query()</c> is tenant-filtered by the platform, so neither path can see or
/// touch another household's row.
/// </summary>
public class BudgetSettingsHandler(IRepository<BudgetSettings> settings, ICurrentTenant tenant, TimeProvider clock)
{
    public async Task<BudgetSettingsResponse?> GetAsync(CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId) return null;

        var row = await settings.Query().FirstOrDefaultAsync(cancellationToken);
        return row is null
            ? BudgetSettingsResponse.From(BudgetSettings.Defaults(tenantId), isDefault: true)
            : BudgetSettingsResponse.From(row, isDefault: false);
    }

    public async Task<(BudgetSettingsResponse? Settings, ErrorResponse? Error)> UpdateAsync(
        UpdateBudgetSettingsRequest request, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId)
            return (null, new ErrorResponse("invalid_token", "No household on the token"));

        if (Validate(request) is { } error) return (null, error);

        var now = clock.GetUtcNow();
        var row = await settings.Query().FirstOrDefaultAsync(cancellationToken);
        var created = row is null;
        row ??= new BudgetSettings { Id = Guid.CreateVersion7(), TenantId = tenantId, CreatedAt = now };

        Apply(row, request, now);
        if (created) await settings.AddAsync(row, cancellationToken);
        else settings.Update(row);

        try
        {
            await settings.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (created)
        {
            // Two first saves raced; the unique (TenantId) index let exactly one insert win. Apply
            // this request on top of the winner instead of failing the caller.
            settings.Remove(row);
            var winner = await settings.Query().FirstAsync(cancellationToken);
            Apply(winner, request, now);
            settings.Update(winner);
            await settings.SaveChangesAsync(cancellationToken);
            row = winner;
        }

        return (BudgetSettingsResponse.From(row, isDefault: false), null);
    }

    /// <summary>The field rules (from donor US-003/US-015); null when the request is valid.</summary>
    public static ErrorResponse? Validate(UpdateBudgetSettingsRequest r)
    {
        if (r.WeekStartWeekday is < 0 or > 6)
            return new ErrorResponse("invalid_request", "week_start_weekday must be between 0 (Sunday) and 6 (Saturday)");
        if (r.MonthAnchor is null || !MonthAnchors.All.Contains(r.MonthAnchor))
            return new ErrorResponse("invalid_request", $"month_anchor must be one of: {string.Join(", ", MonthAnchors.All)}");
        if (r.PrimaryIncome4w < 0 || r.PrimaryIncome5w < 0 || r.SecondaryIncome4w < 0 || r.SecondaryIncome5w < 0)
            return new ErrorResponse("invalid_request", "income amounts cannot be negative");
        if (Currencies.Normalize(r.PrimaryIncomeCurrency) is null)
            return new ErrorResponse("invalid_request", "primary_income_currency must be CRC or USD");
        if (Currencies.Normalize(r.SecondaryIncomeCurrency) is null)
            return new ErrorResponse("invalid_request", "secondary_income_currency must be CRC or USD");
        return null;
    }

    private static void Apply(BudgetSettings row, UpdateBudgetSettingsRequest r, DateTimeOffset now)
    {
        row.WeekStartWeekday = r.WeekStartWeekday;
        row.MonthAnchor = r.MonthAnchor!;
        row.PrimaryIncome4w = r.PrimaryIncome4w;
        row.PrimaryIncome5w = r.PrimaryIncome5w;
        row.PrimaryIncomeCurrency = Currencies.Normalize(r.PrimaryIncomeCurrency)!;
        row.SecondaryIncome4w = r.SecondaryIncome4w;
        row.SecondaryIncome5w = r.SecondaryIncome5w;
        row.SecondaryIncomeCurrency = Currencies.Normalize(r.SecondaryIncomeCurrency)!;
        row.UpdatedAt = now;
    }
}
