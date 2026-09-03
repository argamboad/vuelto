using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Expenses;

/// <summary>The non-generic face of <see cref="ExpenseLineHandler{TLine}"/> the endpoints bind to.</summary>
public interface IExpenseLineHandler
{
    Task<IReadOnlyList<ExpenseResponse>?> ListAsync(bool includeInactive, CancellationToken cancellationToken);
    Task<(ExpenseResponse? Line, ErrorResponse? Error)> CreateAsync(CreateExpenseRequest request, CancellationToken cancellationToken);
    Task<(ExpenseResponse? Line, ErrorResponse? Error)> UpdateAsync(Guid id, UpdateExpenseRequest request, CancellationToken cancellationToken);
    Task<ErrorResponse?> ReorderAsync(ReorderExpenseRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// EXPENSES-1: one behaviour for both budget-line tables (ADR-V007/V008). Lines are never seeded — a
/// household builds its own catalog. Rules: unique name per list, case-insensitively, with the 409
/// reactivation offer; exactly one of the two budgets non-zero; a required active category that backs
/// <b>at most one active line across both lists</b>; an optional active bank; sort order appended on
/// create and owned by <see cref="ReorderAsync"/> (which must name exactly the active set). Foreign ids
/// are not found. <c>Query()</c> is tenant-filtered by the platform.
/// </summary>
public abstract class ExpenseLineHandler<TLine>(
    IRepository<TLine> lines,
    IRepository<FixedExpense> fixedLines,
    IRepository<VariableExpense> variableLines,
    IRepository<Category> categories,
    IRepository<Bank> banks,
    ICurrentTenant tenant,
    TimeProvider clock) : IExpenseLineHandler
    where TLine : class, IExpenseLine
{
    /// <summary>The list this handler serves — <c>fixed</c> / <c>variable</c> — for messages.</summary>
    protected abstract string Kind { get; }

    protected abstract TLine NewLine(Guid tenantId, DateTimeOffset now);

    public async Task<IReadOnlyList<ExpenseResponse>?> ListAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is null) return null;
        var query = includeInactive ? lines.Query() : lines.Query().Where(e => e.IsActive);
        var rows = await query.OrderBy(e => e.SortOrder).ThenBy(e => e.Name).ToListAsync(cancellationToken);
        return rows.Select(ExpenseResponse.From).ToList();
    }

    public async Task<(ExpenseResponse? Line, ErrorResponse? Error)> CreateAsync(CreateExpenseRequest r, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId) return (null, NoTenant());
        var (v, invalid) = await ValidateAsync(r.Name, r.BudgetCrc, r.BudgetUsd, r.PaymentMethod, r.CategoryId, r.BankId, excludeId: null, cancellationToken);
        if (invalid is not null) return (null, invalid);

        if (await FindByNameAsync(v!.Name, cancellationToken) is { } existing)
            return (null, existing.IsActive
                ? new ExpenseConflictResponse("expense_exists", $"A {Kind} expense named '{existing.Name}' already exists", null, null)
                : new ExpenseConflictResponse("expense_exists_inactive", $"'{existing.Name}' already exists but is inactive — reactivate it?", existing.Id, existing.Name));

        var now = clock.GetUtcNow();
        var maxOrder = await lines.Query().Select(e => (int?)e.SortOrder).MaxAsync(cancellationToken);
        var line = NewLine(tenantId, now);
        Apply(line, v, isActive: true, now);
        line.SortOrder = maxOrder is { } m ? m + 1 : 0;
        await lines.AddAsync(line, cancellationToken);
        await lines.SaveChangesAsync(cancellationToken);
        return (ExpenseResponse.From(line), null);
    }

    public async Task<(ExpenseResponse? Line, ErrorResponse? Error)> UpdateAsync(Guid id, UpdateExpenseRequest r, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is null) return (null, NoTenant());

        var line = await lines.Query().FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (line is null) return (null, new ErrorResponse("not_found", $"{Kind} expense not found"));

        var (v, invalid) = await ValidateAsync(r.Name, r.BudgetCrc, r.BudgetUsd, r.PaymentMethod, r.CategoryId, r.BankId, excludeId: id, cancellationToken);
        if (invalid is not null) return (null, invalid);

        if (await FindByNameAsync(v!.Name, cancellationToken) is { } clash && clash.Id != id)
            return (null, new ExpenseConflictResponse("expense_exists", $"A {Kind} expense named '{clash.Name}' already exists", null, null));

        Apply(line, v, r.IsActive, clock.GetUtcNow()); // SortOrder untouched — the reorder endpoint owns it
        lines.Update(line);
        await lines.SaveChangesAsync(cancellationToken);
        return (ExpenseResponse.From(line), null);
    }

    public async Task<ErrorResponse?> ReorderAsync(ReorderExpenseRequest r, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is null) return NoTenant();
        if (r.OrderedIds is null) return Invalid("ordered_ids is required");
        if (r.OrderedIds.Distinct().Count() != r.OrderedIds.Count) return Invalid("ordered_ids must not repeat an id");

        var active = await lines.Query().Where(e => e.IsActive).ToListAsync(cancellationToken);
        if (!active.Select(e => e.Id).ToHashSet().SetEquals(r.OrderedIds))
            return Invalid($"ordered_ids must exactly match the active {Kind} expense lines");

        var now = clock.GetUtcNow();
        var position = 0;
        foreach (var id in r.OrderedIds)
        {
            var line = active.Single(e => e.Id == id);
            line.SortOrder = position++;
            line.UpdatedAt = now;
            lines.Update(line);
        }
        await lines.SaveChangesAsync(cancellationToken); // all positions land together or not at all
        return null;
    }

    private sealed record Valid(string Name, decimal BudgetCrc, decimal BudgetUsd, string PaymentMethod, Guid CategoryId, Guid? BankId);

    private async Task<(Valid? Valid, ErrorResponse? Error)> ValidateAsync(
        string? name, decimal budgetCrc, decimal budgetUsd, string? paymentMethod, Guid? categoryId, Guid? bankId, Guid? excludeId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, Invalid("name is required"));
        if (name.Trim().Length > 100) return (null, Invalid("name must be 100 characters or fewer"));
        if (PaymentMethods.Normalize(paymentMethod) is not { } method) return (null, Invalid("payment_method must be credit_card or bank_account"));
        if (categoryId is not { } category) return (null, Invalid("category_id is required"));
        if (budgetCrc < 0 || budgetUsd < 0) return (null, Invalid("budget_crc and budget_usd cannot be negative"));
        if ((budgetCrc > 0) == (budgetUsd > 0)) return (null, Invalid("exactly one of budget_crc or budget_usd must be non-zero (single-currency lines)"));

        // Tenant-scoped lookups: another household's id simply does not exist.
        if (!await categories.Query().AnyAsync(c => c.Id == category && c.IsActive, cancellationToken)) return (null, Invalid("unknown or inactive category"));
        if (bankId is { } bank && !await banks.Query().AnyAsync(b => b.Id == bank && b.IsActive, cancellationToken)) return (null, Invalid("unknown or inactive bank"));

        // A category backs at most one ACTIVE line across BOTH lists (the dashboard maps a category's spend to one line).
        var inUse = await fixedLines.Query().AnyAsync(e => e.IsActive && e.CategoryId == category && e.Id != excludeId, cancellationToken)
                 || await variableLines.Query().AnyAsync(e => e.IsActive && e.CategoryId == category && e.Id != excludeId, cancellationToken);
        if (inUse) return (null, Invalid("that category already backs another budget line"));

        return (new Valid(name.Trim(), CurrencyMath.Round2(budgetCrc), CurrencyMath.Round2(budgetUsd), method, category, bankId), null);
    }

    private static void Apply(TLine line, Valid v, bool isActive, DateTimeOffset now)
    {
        line.Name = v.Name; line.BudgetCrc = v.BudgetCrc; line.BudgetUsd = v.BudgetUsd; line.PaymentMethod = v.PaymentMethod;
        line.CategoryId = v.CategoryId; line.BankId = v.BankId; line.IsActive = isActive; line.UpdatedAt = now;
    }

    /// <summary>Case-insensitive name match within this list (Postgres <c>lower()</c> on both sides).</summary>
    private Task<TLine?> FindByNameAsync(string name, CancellationToken cancellationToken)
    {
        var lowered = name.ToLowerInvariant();
        return lines.Query().FirstOrDefaultAsync(e => e.Name.ToLower() == lowered, cancellationToken);
    }

    private static ErrorResponse Invalid(string message) => new("invalid_request", message);
    private static ErrorResponse NoTenant() => new("invalid_token", "No household on the token");
}

public sealed class FixedExpenseHandler(
    IRepository<FixedExpense> lines, IRepository<FixedExpense> fixedLines, IRepository<VariableExpense> variableLines,
    IRepository<Category> categories, IRepository<Bank> banks, ICurrentTenant tenant, TimeProvider clock)
    : ExpenseLineHandler<FixedExpense>(lines, fixedLines, variableLines, categories, banks, tenant, clock)
{
    protected override string Kind => "fixed";
    protected override FixedExpense NewLine(Guid tenantId, DateTimeOffset now) => new() { TenantId = tenantId, Name = "", CreatedAt = now, UpdatedAt = now };
}

public sealed class VariableExpenseHandler(
    IRepository<VariableExpense> lines, IRepository<FixedExpense> fixedLines, IRepository<VariableExpense> variableLines,
    IRepository<Category> categories, IRepository<Bank> banks, ICurrentTenant tenant, TimeProvider clock)
    : ExpenseLineHandler<VariableExpense>(lines, fixedLines, variableLines, categories, banks, tenant, clock)
{
    protected override string Kind => "variable";
    protected override VariableExpense NewLine(Guid tenantId, DateTimeOffset now) => new() { TenantId = tenantId, Name = "", CreatedAt = now, UpdatedAt = now };
}
