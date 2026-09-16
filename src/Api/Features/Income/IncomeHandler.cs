using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Income;

/// <summary>
/// INCOME-1 (ADR-V023): the household's income lines — the catalog rules of the budget lines (ADR-V008: unique name
/// per household case-insensitively with the 409 reactivation offer, soft delete, list order owned by
/// <see cref="ReorderAsync"/>) plus the income fields: an optional member who must be in the household, a currency, a
/// kind, a pay period with its amount per payment, and the two pay days of a biweekly line. Never seeded. Any member may
/// edit any line. <c>Query()</c> is tenant-filtered by the platform, so another household's id is simply not found.
/// </summary>
public sealed class IncomeHandler(
    IRepository<IncomeLine> lines,
    ITenantRepository tenants,
    ICurrentTenant tenant,
    TimeProvider clock)
{
    public async Task<IReadOnlyList<IncomeLineResponse>?> ListAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is null) return null;
        var query = includeInactive ? lines.Query() : lines.Query().Where(l => l.IsActive);
        var rows = await query.OrderBy(l => l.SortOrder).ThenBy(l => l.Name).ToListAsync(cancellationToken);
        return rows.Select(IncomeLineResponse.From).ToList();
    }

    public async Task<(IncomeLineResponse? Line, ErrorResponse? Error)> CreateAsync(IncomeLineRequest r, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId) return (null, NoTenant());
        var (v, invalid) = await ValidateAsync(r, tenantId, cancellationToken);
        if (invalid is not null) return (null, invalid);

        if (await FindByNameAsync(v!.Name, cancellationToken) is { } existing)
            return (null, existing.IsActive
                ? new IncomeConflictResponse("income_exists", $"An income named '{existing.Name}' already exists", null, null)
                : new IncomeConflictResponse("income_exists_inactive", $"'{existing.Name}' already exists but is inactive — reactivate it?", existing.Id, existing.Name));

        var now = clock.GetUtcNow();
        var maxOrder = await lines.Query().Select(l => (int?)l.SortOrder).MaxAsync(cancellationToken);
        var line = new IncomeLine { TenantId = tenantId, Name = v.Name, CreatedAt = now };
        Apply(line, v, isActive: true, now);
        line.SortOrder = maxOrder is { } m ? m + 1 : 0;
        await lines.AddAsync(line, cancellationToken);
        await lines.SaveChangesAsync(cancellationToken);
        return (IncomeLineResponse.From(line), null);
    }

    public async Task<(IncomeLineResponse? Line, ErrorResponse? Error)> UpdateAsync(Guid id, IncomeLineRequest r, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId) return (null, NoTenant());

        var line = await lines.Query().FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (line is null) return (null, new ErrorResponse("not_found", "income line not found"));

        var (v, invalid) = await ValidateAsync(r, tenantId, cancellationToken, keepMember: line.MemberUserId);
        if (invalid is not null) return (null, invalid);

        if (await FindByNameAsync(v!.Name, cancellationToken) is { } clash && clash.Id != id)
            return (null, new IncomeConflictResponse("income_exists", $"An income named '{clash.Name}' already exists", null, null));

        Apply(line, v, r.IsActive, clock.GetUtcNow()); // SortOrder untouched — the reorder endpoint owns it
        line.NeedsReview = false;                      // someone looked at it
        lines.Update(line);
        await lines.SaveChangesAsync(cancellationToken);
        return (IncomeLineResponse.From(line), null);
    }

    public async Task<ErrorResponse?> ReorderAsync(ReorderIncomeRequest r, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is null) return NoTenant();
        if (r.OrderedIds is null) return Invalid("ordered_ids is required");
        if (r.OrderedIds.Distinct().Count() != r.OrderedIds.Count) return Invalid("ordered_ids must not repeat an id");

        var active = await lines.Query().Where(l => l.IsActive).ToListAsync(cancellationToken);
        if (!active.Select(l => l.Id).ToHashSet().SetEquals(r.OrderedIds))
            return Invalid("ordered_ids must exactly match the active income lines");

        var now = clock.GetUtcNow();
        var position = 0;
        foreach (var id in r.OrderedIds)
        {
            var line = active.Single(l => l.Id == id);
            line.SortOrder = position++;
            line.UpdatedAt = now;
            lines.Update(line);
        }
        await lines.SaveChangesAsync(cancellationToken); // all positions land together or not at all
        return null;
    }

    private sealed record Valid(string Name, Guid? MemberUserId, string Currency, string Kind, string PayPeriod, decimal Amount, int? PayDay1, int? PayDay2);

    /// <param name="keepMember">
    /// On update, the line's current member is accepted even if they have left the household — the line keeps saying
    /// whose income it was; only a <em>new</em> member must be current.
    /// </param>
    private async Task<(Valid? Valid, ErrorResponse? Error)> ValidateAsync(IncomeLineRequest r, Guid tenantId, CancellationToken cancellationToken, Guid? keepMember = null)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return (null, Invalid("name is required"));
        if (r.Name.Trim().Length > 100) return (null, Invalid("name must be 100 characters or fewer"));
        if (Currencies.Normalize(r.Currency) is not { } currency) return (null, Invalid("currency must be CRC or USD"));
        if (IncomeKinds.Normalize(r.Kind) is not { } kind) return (null, Invalid("kind must be fixed or variable"));
        if (PayPeriods.Normalize(r.PayPeriod) is not { } period) return (null, Invalid("pay_period must be weekly, biweekly or monthly"));
        if (r.Amount <= 0) return (null, Invalid("amount must be greater than zero"));

        int? day1 = null, day2 = null;
        if (period == PayPeriods.Biweekly)
        {
            var days = r.PayDays ?? [PayPeriods.DefaultFirstPayDay, PayPeriods.LastDayOfMonth];
            if (days.Count != 2 || days.Any(d => d is < 1 or > 31) || days[0] == days[1])
                return (null, Invalid("pay_days must be two different days of the month between 1 and 31"));
            (day1, day2) = (Math.Min(days[0], days[1]), Math.Max(days[0], days[1]));
        }
        else if (r.PayDays is { Count: > 0 })
            return (null, Invalid("pay_days only apply to a biweekly line"));

        if (r.MemberUserId is { } member && member != keepMember)
        {
            var members = await tenants.GetMembersAsync(tenantId, cancellationToken);
            if (members.All(m => m.UserId != member)) return (null, Invalid("member_user_id is not a member of this household"));
        }

        return (new Valid(r.Name.Trim(), r.MemberUserId, currency, kind, period, CurrencyMath.Round2(r.Amount), day1, day2), null);
    }

    private static void Apply(IncomeLine line, Valid v, bool isActive, DateTimeOffset now)
    {
        line.Name = v.Name; line.MemberUserId = v.MemberUserId; line.Currency = v.Currency; line.Kind = v.Kind;
        line.PayPeriod = v.PayPeriod; line.Amount = v.Amount; line.PayDay1 = v.PayDay1; line.PayDay2 = v.PayDay2;
        line.IsActive = isActive; line.UpdatedAt = now;
    }

    /// <summary>Case-insensitive name match (Postgres <c>lower()</c> on both sides).</summary>
    private Task<IncomeLine?> FindByNameAsync(string name, CancellationToken cancellationToken)
    {
        var lowered = name.ToLowerInvariant();
        return lines.Query().FirstOrDefaultAsync(l => l.Name.ToLower() == lowered, cancellationToken);
    }

    private static ErrorResponse Invalid(string message) => new("invalid_request", message);
    private static ErrorResponse NoTenant() => new("invalid_token", "No household on the token");
}
