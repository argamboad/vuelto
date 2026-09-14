namespace Vuelto.Core.Budget;

// DASH-1 (port slice P7): the month dashboard's value objects. Every figure is a CRC/USD pair (ADR-V004),
// rounded to 2 dp. Actuals carry each transaction's frozen amounts; projections use the resolved rate.

/// <summary>A monetary value in both currencies (2 dp).</summary>
public record MoneyPair(decimal Crc, decimal Usd)
{
    public static readonly MoneyPair Zero = new(0m, 0m);
}

public record IncomeSummary(MoneyPair Primary, MoneyPair Secondary, MoneyPair Total);

/// <summary>Spend so far, cut two ways: by payment method (Card/Account) and by class (Budgeted/Extraordinary/UnplannedEssential — the waterfall's rows). Both cuts add up to <paramref name="GrandTotal"/>.</summary>
public record ExpenseSummary(MoneyPair Card, MoneyPair Account, MoneyPair GrandTotal, MoneyPair Remainder, MoneyPair Budgeted, MoneyPair Extraordinary, MoneyPair UnplannedEssential);

/// <summary><paramref name="BudgetCurrency"/> is the currency the line is SET in (EXPENSES-1: one side); its other side is a projection at today's rate — over/under must be judged in the native one.</summary>
/// <summary><paramref name="CategoryId"/> is the category the line's actuals are matched on — carried so a client can find the line a purchase will land in.</summary>
public record ExpenseLineSummary(string Name, MoneyPair Budget, MoneyPair Actual, string BudgetCurrency = Currencies.Crc, Guid CategoryId = default);

public record WeeklyTotal(int WeekNumber, DateOnly StartDate, DateOnly EndDate, MoneyPair Total);

public record BalanceSummary(MoneyPair CurrentBalance, MoneyPair RemainderForDebts, MoneyPair PendingBudgeted, MoneyPair ActualRemainder);

/// <summary>An envelope whose reminder applies to this month (ADR-V007: monthly, or five-week months only).</summary>
public record EnvelopeReminder(string Name, MoneyPair AnnualTarget, MoneyPair ContributedThisMonth, MoneyPair Remaining, string Cadence);

/// <summary>A category with expense-class spend but no active budget line.</summary>
/// <summary><paramref name="ByClass"/>: the category's spend split by expense class, so an unbudgeted row can be grouped by what kind of money it is. Sums to <paramref name="Actual"/>.</summary>
public record CategorySpendSummary(string CategoryName, MoneyPair Actual, IReadOnlyList<ClassSpendSummary> ByClass);

/// <summary>One expense class's share of a category's spend.</summary>
public record ClassSpendSummary(string Class, MoneyPair Actual);

/// <summary>Budgeted vs actual for one (bank, payment method) cell. <c>BankId</c> null = the "Unassigned" bucket (bankless lines).</summary>
public record BankMethodBreakdown(Guid? BankId, string BankName, string PaymentMethod, MoneyPair Budget, MoneyPair Actual);

public record DashboardSummary(
    IncomeSummary Income,
    ExpenseSummary Expenses,
    IReadOnlyList<ExpenseLineSummary> FixedExpenses,
    IReadOnlyList<ExpenseLineSummary> VariableExpenses,
    IReadOnlyList<WeeklyTotal> WeeklyExtraordinary,
    IReadOnlyList<WeeklyTotal> WeeklyBudgeted,
    BalanceSummary Balance,
    MoneyPair UnplannedEssentialTotal,
    MoneyPair RefundsTotal,
    IReadOnlyList<EnvelopeReminder> EnvelopeReminders,
    IReadOnlyList<CategorySpendSummary> OtherSpending,
    IReadOnlyList<BankMethodBreakdown> BankMethodBreakdown,
    IReadOnlyList<CardSpendEntry> ByCard);
