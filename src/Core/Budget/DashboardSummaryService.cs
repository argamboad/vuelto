using Vuelto.Core.Entities;

namespace Vuelto.Core.Budget;

/// <summary>
/// Computes the month dashboard (DASH-1; donor US-005/012/018/055). Pure calculation, no I/O.
/// Rate rules (ADR-V006): actual spend always uses each transaction's stored amounts (its frozen rate);
/// projections — income conversion, pending budgeted, remainder for debts, budget display — use the
/// rate passed in, which the caller resolved through the chain. All outputs are 2 dp.
/// </summary>
public interface IDashboardSummaryService
{
    DashboardSummary Calculate(
        Month month,
        IReadOnlyList<Week> weeks,
        IReadOnlyList<Transaction> transactions,
        IReadOnlyList<FixedExpense> fixedExpenses,
        IReadOnlyList<VariableExpense> variableExpenses,
        IReadOnlyList<Refund> refunds,
        IReadOnlyList<Envelope> envelopes,
        FxRates rate, // the day's buy/sell pair (ADR-V019); a bare decimal converts to one rate for both sides
        IReadOnlyDictionary<Guid, string>? categoryNames = null, // ALL categories (a deactivated name still labels "other spending")
        IReadOnlyDictionary<Guid, string>? bankNames = null,    // ALL banks (a deactivated bank still names its cell)
        IReadOnlyDictionary<Guid, string>? cardNames = null);   // ALL cards (CARDS-2: an inactive card still names its row)
}

public sealed class DashboardSummaryService : IDashboardSummaryService
{
    private static bool IsExpenseClass(string type) => TransactionTypes.Expenses.Contains(type);
    private static bool Is(string type, string expected) => string.Equals(type, expected, StringComparison.Ordinal);

    public DashboardSummary Calculate(
        Month month, IReadOnlyList<Week> weeks, IReadOnlyList<Transaction> transactions,
        IReadOnlyList<FixedExpense> fixedExpenses, IReadOnlyList<VariableExpense> variableExpenses,
        IReadOnlyList<Refund> refunds, IReadOnlyList<Envelope> envelopes, FxRates rate,
        IReadOnlyDictionary<Guid, string>? categoryNames = null, IReadOnlyDictionary<Guid, string>? bankNames = null,
        IReadOnlyDictionary<Guid, string>? cardNames = null)
    {
        var income = CalculateIncome(month, transactions, rate);
        var expenses = CalculateExpenseSummary(income, transactions);

        var activeFixed = fixedExpenses.Where(f => f.IsActive).OrderBy(f => f.SortOrder).ToList();
        var activeVariable = variableExpenses.Where(v => v.IsActive).OrderBy(v => v.SortOrder).ToList();
        var fixedLines = activeFixed.Select(f => ToLineSummary(f, transactions, rate)).ToList();
        var variableLines = activeVariable.Select(v => ToLineSummary(v, transactions, rate)).ToList();

        var unplanned = transactions.Where(t => Is(t.TransactionType, TransactionTypes.UnplannedEssential)).ToList();
        var pendingRefunds = refunds.Where(r => !Is(r.Status, RefundStatuses.Received)).ToList(); // a received refund is already income (its inflow)

        return new DashboardSummary(
            income, expenses, fixedLines, variableLines,
            CalculateWeeklyTotals(weeks, transactions, TransactionTypes.Extraordinary),
            CalculateWeeklyTotals(weeks, transactions, TransactionTypes.Budgeted),
            CalculateBalance(income, expenses, rate, activeFixed, activeVariable, fixedLines, variableLines),
            Pair(unplanned.Sum(t => t.AmountCrc), unplanned.Sum(t => t.AmountUsd)),
            Pair(pendingRefunds.Sum(r => r.AmountCrc), pendingRefunds.Sum(r => r.AmountUsd)),
            CalculateEnvelopeReminders(month, envelopes, transactions),
            CalculateOtherSpending(activeFixed, activeVariable, transactions, categoryNames ?? new Dictionary<Guid, string>()),
            CalculateBankMethodBreakdown(activeFixed, activeVariable, transactions, rate, bankNames ?? new Dictionary<Guid, string>()),
            CardSpend.Calculate(transactions, cardNames)); // CARDS-2: the month's spend by card, "no card" last
    }

    // Income (configured incomes at the passed-in rate + inflows' frozen amounts) is the shared
    // IncomeCalculator, so the reports' income donut and this dashboard agree to the cent.
    private static IncomeSummary CalculateIncome(Month month, IReadOnlyList<Transaction> transactions, FxRates rate) =>
        IncomeCalculator.Calculate(month, transactions, rate);

    private static ExpenseSummary CalculateExpenseSummary(IncomeSummary income, IReadOnlyList<Transaction> transactions)
    {
        decimal cardCrc = 0, cardUsd = 0, accountCrc = 0, accountUsd = 0;
        decimal budCrc = 0, budUsd = 0, extCrc = 0, extUsd = 0, unpCrc = 0, unpUsd = 0;
        foreach (var tx in transactions.Where(t => IsExpenseClass(t.TransactionType))) // inflow + envelope_contribution carved out
        {
            if (Is(tx.PaymentMethod, PaymentMethods.BankAccount)) { accountCrc += tx.AmountCrc; accountUsd += tx.AmountUsd; }
            else { cardCrc += tx.AmountCrc; cardUsd += tx.AmountUsd; }
            if (Is(tx.TransactionType, TransactionTypes.Budgeted)) { budCrc += tx.AmountCrc; budUsd += tx.AmountUsd; }
            else if (Is(tx.TransactionType, TransactionTypes.Extraordinary)) { extCrc += tx.AmountCrc; extUsd += tx.AmountUsd; }
            else { unpCrc += tx.AmountCrc; unpUsd += tx.AmountUsd; }
        }
        var grand = Pair(cardCrc + accountCrc, cardUsd + accountUsd);
        return new ExpenseSummary(Pair(cardCrc, cardUsd), Pair(accountCrc, accountUsd), grand, Pair(income.Total.Crc - grand.Crc, income.Total.Usd - grand.Usd),
            Pair(budCrc, budUsd), Pair(extCrc, extUsd), Pair(unpCrc, unpUsd));
    }

    /// <summary>Budget shows both currencies (native + the other at rate); actual sums the line's category's expense-class rows at their frozen amounts.</summary>
    private static ExpenseLineSummary ToLineSummary(IExpenseLine line, IReadOnlyList<Transaction> transactions, FxRates rate)
    {
        var actual = transactions.Where(t => t.CategoryId == line.CategoryId && IsExpenseClass(t.TransactionType)).ToList();
        return new ExpenseLineSummary(line.Name, BudgetPair(line, rate), Pair(actual.Sum(t => t.AmountCrc), actual.Sum(t => t.AmountUsd)),
            line.BudgetCrc > 0 ? Currencies.Crc : Currencies.Usd);
    }

    // A line's budget as a pair is the shared BudgetTotals definition (also behind the reports' "Income vs budget" donut).
    private static MoneyPair BudgetPair(IExpenseLine line, FxRates rate) => BudgetTotals.Pair(line, rate);

    private static List<WeeklyTotal> CalculateWeeklyTotals(IReadOnlyList<Week> weeks, IReadOnlyList<Transaction> transactions, string type) =>
        weeks.OrderBy(w => w.WeekNumber).Select(week =>
        {
            var rows = transactions.Where(t => Is(t.TransactionType, type) && t.TransactionDate >= week.StartDate && t.TransactionDate <= week.EndDate).ToList();
            return new WeeklyTotal(week.WeekNumber, week.StartDate, week.EndDate, Pair(rows.Sum(t => t.AmountCrc), rows.Sum(t => t.AmountUsd)));
        }).ToList();

    private static BalanceSummary CalculateBalance(
        IncomeSummary income, ExpenseSummary expenses, FxRates rate,
        IReadOnlyList<FixedExpense> activeFixed, IReadOnlyList<VariableExpense> activeVariable,
        IReadOnlyList<ExpenseLineSummary> fixedLines, IReadOnlyList<ExpenseLineSummary> variableLines)
    {
        var currentBalance = Pair(income.Total.Crc - expenses.GrandTotal.Crc, income.Total.Usd - expenses.GrandTotal.Usd);

        // Remainder for debts: income − account-paid fixed budgets (mortgage, car loan…), native columns (one side is 0).
        var accountFixed = activeFixed.Where(f => Is(f.PaymentMethod, PaymentMethods.BankAccount)).ToList();
        var accountCrc = accountFixed.Sum(f => f.BudgetCrc);
        var accountUsd = accountFixed.Sum(f => f.BudgetUsd);
        // Budgets are spending targets → spend direction (ADR-V019): $ lines cost colones at Sell, ₡ lines cost dollars at Buy.
        var remainderForDebts = Pair(income.Total.Crc - (accountCrc + rate.SpendUsdToCrc(accountUsd)), income.Total.Usd - (accountUsd + rate.SpendCrcToUsd(accountCrc)));

        // Pending budgeted: the unspent part of each line valued at the live rate. Native column only — the display
        // pair has both sides filled, so summing both would count every line twice.
        var lines = activeFixed.Cast<IExpenseLine>().Concat(activeVariable).ToList();
        var actuals = fixedLines.Concat(variableLines).Select(l => l.Actual).ToList();
        decimal pendingCrc = 0, pendingUsd = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].BudgetCrc > 0) { var n = Math.Max(0m, lines[i].BudgetCrc - actuals[i].Crc); pendingCrc += n; pendingUsd += rate.SpendCrcToUsd(n); }
            else { var n = Math.Max(0m, lines[i].BudgetUsd - actuals[i].Usd); pendingCrc += rate.SpendUsdToCrc(n); pendingUsd += n; }
        }
        var pending = Pair(pendingCrc, pendingUsd);

        return new BalanceSummary(currentBalance, remainderForDebts, pending, Pair(currentBalance.Crc - pending.Crc, currentBalance.Usd - pending.Usd));
    }

    /// <summary>Monthly envelopes always; five-week-month envelopes only when the month has 5 weeks; never inactive ones. Remaining is clamped at 0.</summary>
    private static List<EnvelopeReminder> CalculateEnvelopeReminders(Month month, IReadOnlyList<Envelope> envelopes, IReadOnlyList<Transaction> transactions) =>
        envelopes
            .Where(e => e.IsActive && (Is(e.ReminderCadence, EnvelopeReminderCadences.Monthly) || month.WeekCount == 5))
            .OrderBy(e => e.Name)
            .Select(e =>
            {
                var contributions = transactions.Where(t => Is(t.TransactionType, TransactionTypes.EnvelopeContribution) && t.EnvelopeId == e.Id).ToList();
                var crc = contributions.Sum(t => t.AmountCrc);
                var usd = contributions.Sum(t => t.AmountUsd);
                return new EnvelopeReminder(e.Name, Pair(e.AnnualTargetCrc, e.AnnualTargetUsd), Pair(crc, usd),
                    Pair(Math.Max(0m, e.AnnualTargetCrc - crc), Math.Max(0m, e.AnnualTargetUsd - usd)), e.ReminderCadence);
            })
            .ToList();

    /// <summary>Expense-class spend in categories no active line backs — sorted by CRC descending, then name.</summary>
    private static List<CategorySpendSummary> CalculateOtherSpending(
        IReadOnlyList<FixedExpense> activeFixed, IReadOnlyList<VariableExpense> activeVariable,
        IReadOnlyList<Transaction> transactions, IReadOnlyDictionary<Guid, string> categoryNames)
    {
        var budgeted = activeFixed.Select(f => f.CategoryId).Concat(activeVariable.Select(v => v.CategoryId)).ToHashSet();
        return transactions
            .Where(t => IsExpenseClass(t.TransactionType) && !budgeted.Contains(t.CategoryId))
            .GroupBy(t => t.CategoryId)
            .Select(g => new CategorySpendSummary(categoryNames.GetValueOrDefault(g.Key, g.Key.ToString("N")), Pair(g.Sum(t => t.AmountCrc), g.Sum(t => t.AmountUsd))))
            .OrderByDescending(c => c.Actual.Crc).ThenBy(c => c.CategoryName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Budget by each line's (bank, method); actual by each expense-class row's (bank, method); every cell with either; Unassigned last.</summary>
    private static List<BankMethodBreakdown> CalculateBankMethodBreakdown(
        IReadOnlyList<FixedExpense> activeFixed, IReadOnlyList<VariableExpense> activeVariable,
        IReadOnlyList<Transaction> transactions, FxRates rate, IReadOnlyDictionary<Guid, string> bankNames)
    {
        var budget = new Dictionary<(Guid? BankId, string Method), (decimal Crc, decimal Usd)>();
        foreach (var line in activeFixed.Cast<IExpenseLine>().Concat(activeVariable))
        {
            var pair = BudgetPair(line, rate);
            Accumulate(budget, (line.BankId, line.PaymentMethod), (pair.Crc, pair.Usd));
        }
        var actual = new Dictionary<(Guid? BankId, string Method), (decimal Crc, decimal Usd)>();
        foreach (var tx in transactions.Where(t => IsExpenseClass(t.TransactionType)))
            Accumulate(actual, (tx.BankId, tx.PaymentMethod), (tx.AmountCrc, tx.AmountUsd));

        return budget.Keys.Concat(actual.Keys).Distinct()
            .Select(key =>
            {
                var b = budget.GetValueOrDefault(key);
                var a = actual.GetValueOrDefault(key);
                var name = key.BankId is { } id ? bankNames.GetValueOrDefault(id, "") : "";
                return new BankMethodBreakdown(key.BankId, name, key.Method, Pair(b.Crc, b.Usd), Pair(a.Crc, a.Usd));
            })
            .OrderBy(c => c.BankId is null).ThenBy(c => c.BankName, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.PaymentMethod, StringComparer.Ordinal)
            .ToList();
    }

    private static void Accumulate(Dictionary<(Guid?, string), (decimal Crc, decimal Usd)> map, (Guid?, string) key, (decimal Crc, decimal Usd) add)
    {
        var cur = map.GetValueOrDefault(key);
        map[key] = (cur.Crc + add.Crc, cur.Usd + add.Usd);
    }

    private static decimal DivideSafe(decimal value, decimal rate) => rate == 0 ? 0 : value / rate;

    private static MoneyPair Pair(decimal crc, decimal usd) => new(CurrencyMath.Round2(crc), CurrencyMath.Round2(usd));
}
