namespace Vuelto.Core.Budget;

/// <summary>
/// The shared shape of a budget line (EXPENSES-1, ADR-V007): a named dual-currency budget mapped to the
/// category whose transactions count as its actual spend. <c>FixedExpense</c> and <c>VariableExpense</c>
/// are structurally identical tables, so one generic handler serves both. A line is a catalog entry
/// (soft-deleted, unique name per household, 409 reactivation offer — ADR-V008) with a
/// <see cref="SortOrder"/> the reorder endpoint owns. Exactly one of the two budgets is non-zero.
/// </summary>
public interface IExpenseLine : ICatalogEntry
{
    decimal BudgetCrc { get; set; }
    decimal BudgetUsd { get; set; }

    /// <summary>One of <see cref="PaymentMethods"/> — how this line is normally paid.</summary>
    string PaymentMethod { get; set; }

    /// <summary>Position within its list; assigned by the reorder endpoint, appended on create.</summary>
    int SortOrder { get; set; }

    /// <summary>Required — a line without a category could never show where the budget is going.</summary>
    Guid CategoryId { get; set; }

    /// <summary>Optional bank the line is paid from (donor US-054). Unlike a transaction's required bank, a plan may stay bank-agnostic: null = "Unassigned".</summary>
    Guid? BankId { get; set; }
}
