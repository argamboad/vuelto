using Vuelto.Core.Budget;
using Vuelto.Core.Entities;

namespace Vuelto.Core.Tests.Budget;

/// <summary>
/// DASH-1 (donor US-005/012/018/055 spec, ported verbatim onto the Core entities). Fixture: June 2026,
/// four weeks from May 28, income 3000 + 500 USD, rate 500 passed in by the caller (ADR-V006: months
/// store no rate). Actual spend uses each transaction's frozen amounts; projections use the passed rate.
/// </summary>
public class DashboardSummaryServiceTests
{
    private readonly DashboardSummaryService _service = new();

    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid MonthId = Guid.NewGuid();
    private static readonly Guid GroceriesCat = Guid.NewGuid();
    private static readonly Guid MortgageCat = Guid.NewGuid();
    private static readonly Guid DiningCat = Guid.NewGuid();
    private static readonly Guid CarLoanCat = Guid.NewGuid();
    private static readonly Guid BankId = Guid.NewGuid();

    private static Month GetMonth() => new()
    {
        Id = MonthId, TenantId = TenantId, Year = 2026, MonthNumber = 6, WeekCount = 4,
        Week1StartDate = new DateOnly(2026, 5, 28),
    };

    /// <summary>The month's income: $3,000 and $500 (INCOME-1 rows).</summary>
    private static List<MonthIncome> GetIncome(decimal primary = 3000m, string primaryCurrency = "USD") =>
    [
        new() { TenantId = TenantId, MonthId = MonthId, Label = "Primary income", Amount = primary, Currency = primaryCurrency, SortOrder = 0 },
        new() { TenantId = TenantId, MonthId = MonthId, Label = "Secondary income", Amount = 500m, Currency = "USD", SortOrder = 1 },
    ];

    private static List<Week> GetWeeks() =>
    [
        new() { MonthId = MonthId, TenantId = TenantId, WeekNumber = 1, StartDate = new DateOnly(2026, 5, 28), EndDate = new DateOnly(2026, 6, 3) },
        new() { MonthId = MonthId, TenantId = TenantId, WeekNumber = 2, StartDate = new DateOnly(2026, 6, 4), EndDate = new DateOnly(2026, 6, 10) },
        new() { MonthId = MonthId, TenantId = TenantId, WeekNumber = 3, StartDate = new DateOnly(2026, 6, 11), EndDate = new DateOnly(2026, 6, 17) },
        new() { MonthId = MonthId, TenantId = TenantId, WeekNumber = 4, StartDate = new DateOnly(2026, 6, 18), EndDate = new DateOnly(2026, 6, 24) }
    ];

    private static Transaction Tx(Guid categoryId, decimal crc, decimal usd, string type, DateOnly date, string method = "credit_card") => new()
    {
        TenantId = TenantId, MonthId = MonthId, BankId = BankId, Payee = "Test", PaymentMethod = method,
        OriginalAmount = crc, Currency = "CRC", TransactionDate = date, CategoryId = categoryId,
        AmountCrc = crc, AmountUsd = usd, ExchangeRateUsed = usd == 0 ? 500m : crc / usd, TransactionType = type
    };

    private static List<Transaction> GetTransactions() =>
    [
        Tx(GroceriesCat, 100_000m, 200m, "budgeted", new DateOnly(2026, 5, 30)),                 // week 1, card
        Tx(MortgageCat, 300_000m, 600m, "budgeted", new DateOnly(2026, 6, 5), "bank_account"),   // week 2, account
        Tx(DiningCat, 25_000m, 50m, "extraordinary", new DateOnly(2026, 6, 1)),                  // week 1, card
        Tx(DiningCat, 10_000m, 20m, "extraordinary", new DateOnly(2026, 6, 12))                  // week 3, card
    ];

    private static List<FixedExpense> GetFixedExpenses() =>
    [
        new() { TenantId = TenantId, Name = "Mortgage", BudgetCrc = 350_000m, PaymentMethod = "bank_account", CategoryId = MortgageCat, SortOrder = 1 },
        new() { TenantId = TenantId, Name = "Car loan", BudgetUsd = 400m, PaymentMethod = "bank_account", CategoryId = CarLoanCat, SortOrder = 2 },
        new() { TenantId = TenantId, Name = "Inactive line", BudgetCrc = 999_999m, PaymentMethod = "bank_account", CategoryId = CarLoanCat, IsActive = false, SortOrder = 3 }
    ];

    private static List<VariableExpense> GetVariableExpenses() =>
        [new() { TenantId = TenantId, Name = "Groceries", BudgetCrc = 150_000m, PaymentMethod = "credit_card", CategoryId = GroceriesCat, SortOrder = 1 }];

    private DashboardSummary Calculate() => _service.Calculate(GetMonth(), GetIncome(), GetWeeks(), GetTransactions(), GetFixedExpenses(), GetVariableExpenses(), [], [], 500m);

    private DashboardSummary With(List<Transaction>? transactions = null, List<FixedExpense>? fixedLines = null, List<VariableExpense>? variableLines = null,
        List<Refund>? refunds = null, List<Envelope>? envelopes = null, decimal rate = 500m, Month? month = null, List<MonthIncome>? income = null,
        IReadOnlyDictionary<Guid, string>? categories = null, IReadOnlyDictionary<Guid, string>? banks = null, IReadOnlyDictionary<Guid, CardLabel>? cards = null) =>
        _service.Calculate(month ?? GetMonth(), income ?? GetIncome(), GetWeeks(), transactions ?? [], fixedLines ?? [], variableLines ?? [], refunds ?? [], envelopes ?? [], rate, categories, banks, cards);

    private static Refund RefundExpected(decimal crc, decimal usd, string status) => new()
    {
        TenantId = TenantId, MonthId = MonthId, TransactionId = Guid.NewGuid(), Payee = "Test",
        TransactionDate = new DateOnly(2026, 6, 4), Percentage = 50m, AmountCrc = crc, AmountUsd = usd, Status = status
    };

    [Fact]
    public void LineSummary_CarriesTheCurrencyTheLineIsSetIn()
    {
        var lines = Calculate().FixedExpenses;
        Assert.Equal("CRC", Assert.Single(lines, l => l.Name == "Mortgage").BudgetCurrency);  // BudgetCrc 350,000
        Assert.Equal("USD", Assert.Single(lines, l => l.Name == "Car loan").BudgetCurrency);  // BudgetUsd 400 — its ₡ side is a projection
    }

    // ---- unplanned essentials, refunds & envelopes ----

    [Fact]
    public void UnplannedEssential_TotalSlicesThatClass_AndStillCountsInExpenses()
    {
        var summary = With(transactions:
        [
            Tx(GroceriesCat, 20_000m, 40m, "unplanned_essential", new DateOnly(2026, 6, 3)),
            Tx(DiningCat, 55_000m, 110m, "unplanned_essential", new DateOnly(2026, 6, 9)),
            Tx(GroceriesCat, 100_000m, 200m, "budgeted", new DateOnly(2026, 6, 5))
        ]);

        Assert.Equal((75_000m, 150m), (summary.UnplannedEssentialTotal.Crc, summary.UnplannedEssentialTotal.Usd));
        Assert.Equal(175_000m, summary.Expenses.GrandTotal.Crc);
        // The waterfall's class cut adds up to the same total as the payment-method cut.
        Assert.Equal((100_000m, 0m, 75_000m), (summary.Expenses.Budgeted.Crc, summary.Expenses.Extraordinary.Crc, summary.Expenses.UnplannedEssential.Crc));
    }

    [Fact]
    public void Refunds_TotalAggregatesPendingOnly_ExcludesReceived()
    {
        var summary = With(refunds: [RefundExpected(10_000m, 20m, "pending"), RefundExpected(5_000m, 10m, "received")]);
        Assert.Equal((10_000m, 20m), (summary.RefundsTotal.Crc, summary.RefundsTotal.Usd));
    }

    [Fact]
    public void Refunds_NeverTouchBalanceOrExpenses()
    {
        var baseline = Calculate();
        var withRefunds = _service.Calculate(GetMonth(), GetIncome(), GetWeeks(), GetTransactions(), GetFixedExpenses(), GetVariableExpenses(), [RefundExpected(88_888m, 170m, "pending")], [], 500m);

        Assert.Equal(baseline.Balance.CurrentBalance, withRefunds.Balance.CurrentBalance);
        Assert.Equal(baseline.Expenses.GrandTotal, withRefunds.Expenses.GrandTotal);
        Assert.Equal(baseline.Balance.ActualRemainder, withRefunds.Balance.ActualRemainder);
    }

    private static readonly Guid MarchamoEnvelopeId = Guid.NewGuid();
    private static readonly Guid ImpuestosEnvelopeId = Guid.NewGuid();

    private static List<Envelope> GetEnvelopes() =>
    [
        new() { Id = MarchamoEnvelopeId, TenantId = TenantId, Name = "Marchamo", AnnualTargetCrc = 718_000m, ReminderCadence = "monthly" },
        new() { Id = ImpuestosEnvelopeId, TenantId = TenantId, Name = "Impuestos", AnnualTargetCrc = 164_000m, ReminderCadence = "five_week_months" },
        new() { TenantId = TenantId, Name = "Inactive bucket", AnnualTargetCrc = 1m, ReminderCadence = "monthly", IsActive = false }
    ];

    [Fact]
    public void EnvelopeReminders_FourWeekMonth_ShowsOnlyMonthlyCadence()
    {
        var reminder = Assert.Single(With(envelopes: GetEnvelopes()).EnvelopeReminders);
        Assert.Equal(("Marchamo", 718_000m, 0m, 718_000m, "monthly"), (reminder.Name, reminder.AnnualTarget.Crc, reminder.ContributedThisMonth.Crc, reminder.Remaining.Crc, reminder.Cadence));
    }

    [Fact]
    public void EnvelopeReminders_FiveWeekMonth_IncludesFiveWeekBuckets_NeverInactive()
    {
        var month = GetMonth();
        month.WeekCount = 5;
        var reminders = With(envelopes: GetEnvelopes(), month: month).EnvelopeReminders;

        Assert.Equal(2, reminders.Count);
        Assert.Contains(reminders, e => e.Name == "Impuestos");
        Assert.DoesNotContain(reminders, e => e.Name == "Inactive bucket");
    }

    [Fact]
    public void FrozenActualsVsLiveProjections_DivergentRates_ActualsStayFrozen()
    {
        // A transaction frozen at 500 while the live rate is 1000: actuals keep the stored USD, income converts at 1000.
        var summary = With(transactions: [Tx(GroceriesCat, 50_000m, 100m, "budgeted", new DateOnly(2026, 6, 3))], rate: 1000m);

        Assert.Equal((50_000m, 100m), (summary.Expenses.GrandTotal.Crc, summary.Expenses.GrandTotal.Usd));
        Assert.Equal((3_500_000m, 3500m), (summary.Income.Total.Crc, summary.Income.Total.Usd));
    }

    [Fact]
    public void TwoRates_IncomeConvertsAtBuy_BudgetLinesAtTheSpendingSide()
    {
        // ADR-V019 with the BCCR pair (compra 448.27 / venta 453.69): dollar income is worth colones at compra;
        // a $ budget line costs colones at venta; a ₡ budget line is worth dollars at compra.
        var summary = _service.Calculate(GetMonth(), GetIncome(), GetWeeks(), GetTransactions(), GetFixedExpenses(), GetVariableExpenses(), [], [], new FxRates(Buy: 448.27m, Sell: 453.69m));

        Assert.Equal((1_344_810m, 3000m), (summary.Income.Rows[0].Pair.Crc, summary.Income.Rows[0].Pair.Usd));
        Assert.Equal((1_568_945m, 3500m), (summary.Income.Total.Crc, summary.Income.Total.Usd));
        var mortgage = summary.FixedExpenses.Single(l => l.Name == "Mortgage");
        var carLoan = summary.FixedExpenses.Single(l => l.Name == "Car loan");
        Assert.Equal((350_000m, 780.78m), (mortgage.Budget.Crc, mortgage.Budget.Usd));   // ₡350,000 / 448.27
        Assert.Equal((181_476m, 400m), (carLoan.Budget.Crc, carLoan.Budget.Usd));         // $400 × 453.69
        // Actuals never move: they are each transaction's frozen amounts.
        Assert.Equal((435_000m, 870m), (summary.Expenses.GrandTotal.Crc, summary.Expenses.GrandTotal.Usd));
    }

    [Fact]
    public void Income_ConvertsUsdToCrcAtPassedInRate()
    {
        var summary = Calculate();
        Assert.Equal((3000m, 1_500_000m), (summary.Income.Rows[0].Pair.Usd, summary.Income.Rows[0].Pair.Crc));
        Assert.Equal((500m, 250_000m), (summary.Income.Rows[1].Pair.Usd, summary.Income.Rows[1].Pair.Crc));
        Assert.Equal((3500m, 1_750_000m), (summary.Income.Total.Usd, summary.Income.Total.Crc));
    }

    [Fact]
    public void Income_CrcDenominatedIncome_ConvertsToUsdAtPassedInRate()
    {
        var summary = With(income: GetIncome(1_500_000m, "CRC"));

        Assert.Equal((1_500_000m, 3000m), (summary.Income.Rows[0].Pair.Crc, summary.Income.Rows[0].Pair.Usd));
        Assert.Equal((250_000m, 500m), (summary.Income.Rows[1].Pair.Crc, summary.Income.Rows[1].Pair.Usd));
        Assert.Equal((1_750_000m, 3500m), (summary.Income.Total.Crc, summary.Income.Total.Usd));
    }

    [Fact]
    public void ExpenseSummary_SplitsCardAndAccountByTransactionPaymentMethod()
    {
        var summary = Calculate();
        Assert.Equal((135_000m, 270m), (summary.Expenses.Card.Crc, summary.Expenses.Card.Usd));
        Assert.Equal((300_000m, 600m), (summary.Expenses.Account.Crc, summary.Expenses.Account.Usd));
        Assert.Equal((435_000m, 870m), (summary.Expenses.GrandTotal.Crc, summary.Expenses.GrandTotal.Usd));
        Assert.Equal((1_315_000m, 2630m), (summary.Expenses.Remainder.Crc, summary.Expenses.Remainder.Usd));
    }

    [Fact]
    public void FixedExpenseLines_ShowBudgetedVsActual_ActiveOnly_InSortOrder()
    {
        var summary = Calculate();

        Assert.Equal(2, summary.FixedExpenses.Count);
        var mortgage = summary.FixedExpenses[0];
        Assert.Equal(("Mortgage", 350_000m, 300_000m, 600m), (mortgage.Name, mortgage.Budget.Crc, mortgage.Actual.Crc, mortgage.Actual.Usd));
        var carLoan = summary.FixedExpenses[1];
        Assert.Equal(("Car loan", 400m, 0m), (carLoan.Name, carLoan.Budget.Usd, carLoan.Actual.Crc));
    }

    [Fact]
    public void VariableExpenseLines_MatchActualsByCategory()
    {
        var groceries = Assert.Single(Calculate().VariableExpenses);
        Assert.Equal((150_000m, 100_000m, 200m), (groceries.Budget.Crc, groceries.Actual.Crc, groceries.Actual.Usd));
    }

    [Fact]
    public void WeeklyExtraordinary_OneRowPerWeekWithDateRanges()
    {
        var weekly = Calculate().WeeklyExtraordinary;
        Assert.Equal(4, weekly.Count);
        Assert.Equal(new DateOnly(2026, 5, 28), weekly[0].StartDate);
        Assert.Equal([25_000m, 0m, 10_000m, 0m], weekly.Select(w => w.Total.Crc));
        Assert.Equal(20m, weekly[2].Total.Usd);
    }

    [Fact]
    public void WeeklyBudgeted_OneRowPerWeekWithDateRanges()
    {
        var weekly = Calculate().WeeklyBudgeted;
        Assert.Equal(4, weekly.Count);
        Assert.Equal([100_000m, 300_000m, 0m, 0m], weekly.Select(w => w.Total.Crc));
        Assert.Equal(600m, weekly[1].Total.Usd);
    }

    [Fact]
    public void Balance_CurrentBalance_IsIncomeMinusActualSpend()
    {
        var summary = Calculate();
        Assert.Equal((1_315_000m, 2630m), (summary.Balance.CurrentBalance.Crc, summary.Balance.CurrentBalance.Usd));
    }

    [Fact]
    public void Balance_RemainderForDebts_IsIncomeMinusActiveAccountFixedBudgets()
    {
        // Mortgage 350,000 CRC + Car loan 400 USD (= 200,000 CRC); USD view 700 + 400.
        var summary = Calculate();
        Assert.Equal(1_750_000m - 550_000m, summary.Balance.RemainderForDebts.Crc);
        Assert.Equal(3500m - 1100m, summary.Balance.RemainderForDebts.Usd);
    }

    [Fact]
    public void Balance_PendingBudgeted_UsesLiveRateForUnspentAmounts()
    {
        // Pending: Mortgage 50,000 CRC, Car loan 400 USD, Groceries 50,000 CRC.
        var summary = Calculate();
        Assert.Equal(50_000m + 50_000m + 400m * 500m, summary.Balance.PendingBudgeted.Crc);
        Assert.Equal(400m + 100_000m / 500m, summary.Balance.PendingBudgeted.Usd);
    }

    [Fact]
    public void Balance_ActualRemainder_IsCurrentBalanceMinusPending()
    {
        var summary = Calculate();
        Assert.Equal(1_315_000m - 300_000m, summary.Balance.ActualRemainder.Crc);
        Assert.Equal(2630m - 600m, summary.Balance.ActualRemainder.Usd);
    }

    [Fact]
    public void EmptyMonth_NoTransactions_ActualsZero_FullBudgetsPending()
    {
        var summary = With(fixedLines: GetFixedExpenses(), variableLines: GetVariableExpenses());

        Assert.Equal(0m, summary.Expenses.GrandTotal.Crc);
        Assert.All(summary.FixedExpenses, l => Assert.Equal(0m, l.Actual.Crc));
        Assert.All(summary.WeeklyExtraordinary, w => Assert.Equal(0m, w.Total.Crc));
        Assert.Equal(500_000m + 200_000m, summary.Balance.PendingBudgeted.Crc);
        Assert.Equal(1_750_000m, summary.Balance.CurrentBalance.Crc);
    }

    [Fact]
    public void EmptyMonth_NoLinesNoTransactions_AllZeros()
    {
        var summary = With();
        Assert.Empty(summary.FixedExpenses);
        Assert.Empty(summary.VariableExpenses);
        Assert.Equal(0m, summary.Balance.PendingBudgeted.Crc);
        Assert.Equal(summary.Income.Total.Crc, summary.Balance.ActualRemainder.Crc);
    }

    // ---- inflow ----

    [Theory]
    [InlineData("budgeted")]
    [InlineData("extraordinary")]
    [InlineData("unplanned_essential")]
    public void NonInflowClasses_CountAsExpenses_NotAddedToIncome(string type)
    {
        var summary = With(transactions: [Tx(GroceriesCat, 50_000m, 100m, type, new DateOnly(2026, 6, 3))]);
        Assert.Equal(50_000m, summary.Expenses.GrandTotal.Crc);
        Assert.Equal(1_750_000m, summary.Income.Total.Crc);
    }

    [Fact]
    public void Inflow_FoldsIntoIncome_ExcludedFromExpenses()
    {
        var summary = With(transactions: [Tx(GroceriesCat, 30_000m, 60m, "inflow", new DateOnly(2026, 6, 3)), Tx(DiningCat, 10_000m, 20m, "budgeted", new DateOnly(2026, 6, 3))]);
        Assert.Equal((1_780_000m, 3560m), (summary.Income.Total.Crc, summary.Income.Total.Usd));
        Assert.Equal((10_000m, 20m), (summary.Expenses.GrandTotal.Crc, summary.Expenses.GrandTotal.Usd));
    }

    [Fact]
    public void Inflow_MultipleInflows_AllAddedToIncome()
    {
        var summary = With(transactions: [Tx(GroceriesCat, 20_000m, 40m, "inflow", new DateOnly(2026, 6, 3)), Tx(DiningCat, 15_000m, 30m, "inflow", new DateOnly(2026, 6, 5))]);
        Assert.Equal((1_785_000m, 3570m), (summary.Income.Total.Crc, summary.Income.Total.Usd));
        Assert.Equal(0m, summary.Expenses.GrandTotal.Crc);
    }

    [Fact]
    public void Inflow_CurrentBalance_IncludesInflowInIncome()
    {
        var summary = With(transactions: [Tx(GroceriesCat, 50_000m, 100m, "inflow", new DateOnly(2026, 6, 3)), Tx(DiningCat, 10_000m, 20m, "budgeted", new DateOnly(2026, 6, 3))]);
        Assert.Equal((1_790_000m, 3580m), (summary.Balance.CurrentBalance.Crc, summary.Balance.CurrentBalance.Usd));
    }

    // ---- envelope_contribution ----

    private static Transaction ContributionTx(Guid envelopeId, decimal crc, decimal usd, DateOnly date)
    {
        var tx = Tx(GroceriesCat, crc, usd, "envelope_contribution", date, "bank_account");
        tx.EnvelopeId = envelopeId;
        return tx;
    }

    [Fact]
    public void EnvelopeContribution_NotCountedInExpenses()
    {
        var summary = With(transactions: [ContributionTx(MarchamoEnvelopeId, 50_000m, 100m, new DateOnly(2026, 6, 3)), Tx(DiningCat, 10_000m, 20m, "budgeted", new DateOnly(2026, 6, 3))], envelopes: GetEnvelopes());
        Assert.Equal((10_000m, 20m), (summary.Expenses.GrandTotal.Crc, summary.Expenses.GrandTotal.Usd));
    }

    [Fact]
    public void EnvelopeContribution_NotAddedToIncome()
    {
        var summary = With(transactions: [ContributionTx(MarchamoEnvelopeId, 50_000m, 100m, new DateOnly(2026, 6, 3))], envelopes: GetEnvelopes());
        Assert.Equal((1_750_000m, 3500m), (summary.Income.Total.Crc, summary.Income.Total.Usd));
    }

    [Fact]
    public void EnvelopeReminder_ShowsContributedThisMonth()
    {
        var summary = With(transactions: [ContributionTx(MarchamoEnvelopeId, 30_000m, 60m, new DateOnly(2026, 6, 3)), ContributionTx(MarchamoEnvelopeId, 20_000m, 40m, new DateOnly(2026, 6, 10))], envelopes: GetEnvelopes());
        var reminder = Assert.Single(summary.EnvelopeReminders);
        Assert.Equal(("Marchamo", 50_000m, 100m), (reminder.Name, reminder.ContributedThisMonth.Crc, reminder.ContributedThisMonth.Usd));
    }

    [Fact]
    public void EnvelopeReminder_Remaining_IsTargetMinusContributed_ClampedAtZero()
    {
        var summary = With(transactions: [ContributionTx(MarchamoEnvelopeId, 50_000m, 100m, new DateOnly(2026, 6, 3))], envelopes: GetEnvelopes());
        var reminder = Assert.Single(summary.EnvelopeReminders);
        Assert.Equal(718_000m - 50_000m, reminder.Remaining.Crc);
        Assert.Equal(0m, reminder.Remaining.Usd); // no USD target → clamped, never negative
    }

    [Fact]
    public void EnvelopeReminder_ContributionForOtherEnvelope_NotCounted()
    {
        var summary = With(transactions: [ContributionTx(ImpuestosEnvelopeId, 40_000m, 80m, new DateOnly(2026, 6, 3))], envelopes: GetEnvelopes());
        var reminder = Assert.Single(summary.EnvelopeReminders);
        Assert.Equal(("Marchamo", 0m, 718_000m), (reminder.Name, reminder.ContributedThisMonth.Crc, reminder.Remaining.Crc));
    }

    [Fact]
    public void AllAmounts_RoundedToTwoDecimals()
    {
        List<MonthIncome> income = [new() { TenantId = TenantId, MonthId = MonthId, Label = "Salary", Amount = 1234.56m, Currency = "USD" }];
        var summary = With(income: income, rate: 511.37m);

        Assert.Equal(631_316.95m, summary.Income.Total.Crc); // 1234.56 × 511.37 = 631,316.9472
        Assert.Equal(decimal.Round(summary.Balance.CurrentBalance.Crc, 2), summary.Balance.CurrentBalance.Crc);
    }

    // ---- dual-currency budget display ----

    [Fact]
    public void BudgetDisplay_CrcNativeLine_FillsBothCurrencies()
    {
        var mortgage = Calculate().FixedExpenses[0];
        Assert.Equal((350_000m, 700m), (mortgage.Budget.Crc, mortgage.Budget.Usd));
    }

    [Fact]
    public void BudgetDisplay_UsdNativeLine_FillsBothCurrencies()
    {
        var carLoan = Calculate().FixedExpenses[1];
        Assert.Equal((200_000m, 400m), (carLoan.Budget.Crc, carLoan.Budget.Usd));
    }

    [Fact]
    public void PendingBudgeted_CrossCurrencyDoubleCountGuard()
    {
        // Both display columns are filled; pending must use the native column only (₡100,000 / $200, never $400).
        var summary = With(fixedLines: [new() { TenantId = TenantId, Name = "Rent", BudgetCrc = 100_000m, PaymentMethod = "bank_account", CategoryId = Guid.NewGuid(), SortOrder = 1 }]);
        Assert.Equal((100_000m, 200m), (summary.Balance.PendingBudgeted.Crc, summary.Balance.PendingBudgeted.Usd));
    }

    // ---- other spending ----

    private static readonly Guid RestaurantCat = Guid.NewGuid();
    private static readonly Guid TransportCat = Guid.NewGuid();

    private static IReadOnlyDictionary<Guid, string> GetCategories() => new Dictionary<Guid, string>
    {
        [GroceriesCat] = "Groceries", [MortgageCat] = "Mortgage", [DiningCat] = "Dining", [CarLoanCat] = "Car Loan", [RestaurantCat] = "Restaurant", [TransportCat] = "Transport"
    };

    private DashboardSummary WithCatalog(List<Transaction>? transactions = null, List<FixedExpense>? fixedLines = null) =>
        With(transactions: transactions ?? GetTransactions(), fixedLines: fixedLines ?? GetFixedExpenses(), variableLines: GetVariableExpenses(), categories: GetCategories());

    [Fact]
    public void OtherSpending_UnbudgetedCategory_AppearsWithActual()
    {
        var dining = Assert.Single(WithCatalog().OtherSpending);
        Assert.Equal(("Dining", 35_000m, 70m), (dining.CategoryName, dining.Actual.Crc, dining.Actual.Usd));
    }

    [Fact]
    public void OtherSpending_BudgetedCategory_AbsentFromOtherSpending()
    {
        var other = WithCatalog().OtherSpending;
        Assert.DoesNotContain(other, c => c.CategoryName == "Groceries");
        Assert.DoesNotContain(other, c => c.CategoryName == "Mortgage");
    }

    [Fact]
    public void OtherSpending_DeactivatedLine_CategoryReappearsInOtherSpending()
    {
        var lines = GetFixedExpenses();
        lines.Single(f => f.Name == "Mortgage").IsActive = false;

        var mortgage = WithCatalog(fixedLines: lines).OtherSpending.Single(c => c.CategoryName == "Mortgage");
        Assert.Equal(300_000m, mortgage.Actual.Crc);
    }

    [Fact]
    public void OtherSpending_CategoryName_ResolvesFromTheAllStatesLookup()
    {
        var summary = With(transactions: [Tx(RestaurantCat, 15_000m, 30m, "extraordinary", new DateOnly(2026, 6, 5))], categories: GetCategories());
        Assert.Equal("Restaurant", Assert.Single(summary.OtherSpending).CategoryName);
    }

    [Fact]
    public void OtherSpending_InflowAndEnvelopeContribution_Excluded()
    {
        var summary = With(transactions:
        [
            Tx(RestaurantCat, 20_000m, 40m, "inflow", new DateOnly(2026, 6, 5)),
            Tx(TransportCat, 10_000m, 20m, "envelope_contribution", new DateOnly(2026, 6, 5)),
            Tx(DiningCat, 5_000m, 10m, "extraordinary", new DateOnly(2026, 6, 5))
        ], categories: GetCategories());

        Assert.Equal("Dining", Assert.Single(summary.OtherSpending).CategoryName);
    }

    [Fact]
    public void OtherSpending_SortedByActualCrcDescThenName()
    {
        var summary = With(transactions:
        [
            Tx(TransportCat, 5_000m, 10m, "extraordinary", new DateOnly(2026, 6, 5)),
            Tx(RestaurantCat, 20_000m, 40m, "extraordinary", new DateOnly(2026, 6, 5)),
            Tx(DiningCat, 20_000m, 40m, "extraordinary", new DateOnly(2026, 6, 5))
        ], categories: GetCategories());

        Assert.Equal(["Dining", "Restaurant", "Transport"], summary.OtherSpending.Select(c => c.CategoryName));
    }

    [Fact]
    public void OtherSpending_ReconciliationInvariant_SumEqualsExpenseGrandTotal()
    {
        var summary = WithCatalog();
        var budgetActuals = summary.FixedExpenses.Sum(l => l.Actual.Crc) + summary.VariableExpenses.Sum(l => l.Actual.Crc);
        Assert.Equal(summary.Expenses.GrandTotal.Crc, budgetActuals + summary.OtherSpending.Sum(c => c.Actual.Crc));
    }

    // ---- by bank (actuals) and by payment method (budget vs actual) ----
    // Owner decision (2026-09-14): a budget line names no bank — a plan is "pay by card / by account", not
    // "pay from BAC"; the transaction records the real bank. So the bank cut is actuals only, and the
    // budget-vs-actual comparison lives on the method axis, where it stays honest.

    private static readonly Guid BacBankId = Guid.NewGuid();
    private static readonly Guid CashBankId = Guid.NewGuid();

    private static Transaction WithBank(Transaction tx, Guid bankId) { tx.BankId = bankId; return tx; }

    private static FixedExpense FixedLine(string name, decimal crc, decimal usd, string method, Guid category, int order) =>
        new() { TenantId = TenantId, Name = name, BudgetCrc = crc, BudgetUsd = usd, PaymentMethod = method, CategoryId = category, SortOrder = order };

    [Fact]
    public void ByCard_GroupsTheMonthsExpenseRowsByCard_NoCardLast()
    {
        // CARDS-2: the dashboard's "By card" table — the same rows the bank table sums, cut by the card that paid.
        var visa = Guid.NewGuid();
        Transaction OnCard(Transaction tx, Guid? card) { tx.CardId = card; return tx; }
        var rows = With(transactions:
        [
            OnCard(Tx(DiningCat, 5_000m, 10m, "extraordinary", new DateOnly(2026, 6, 5)), visa),
            OnCard(Tx(DiningCat, 7_000m, 14m, "budgeted", new DateOnly(2026, 6, 6)), visa),
            OnCard(Tx(GroceriesCat, 3_000m, 6m, "inflow", new DateOnly(2026, 6, 7)), visa),
            Tx(GroceriesCat, 9_000m, 18m, "budgeted", new DateOnly(2026, 6, 8), "bank_account")
        ], cards: new Dictionary<Guid, CardLabel> { [visa] = new("Allan's Visa", CardKinds.Debit) }).ByCard;

        Assert.Equal(2, rows.Count);
        Assert.Equal(("Allan's Visa", 12_000m, 24m, 2), (rows[0].CardName, rows[0].TotalCrc, rows[0].TotalUsd, rows[0].Count));
        Assert.Equal((null, 9_000m, 1), (rows[1].CardId, rows[1].TotalCrc, rows[1].Count));
    }

    [Fact]
    public void BankMethodBreakdown_GroupsActualByTransactionBankAndMethod_ExcludesInflow_BankNameOrder()
    {
        var rows = With(transactions:
        [
            WithBank(Tx(DiningCat, 5_000m, 10m, "extraordinary", new DateOnly(2026, 6, 5)), CashBankId),
            WithBank(Tx(DiningCat, 7_000m, 14m, "budgeted", new DateOnly(2026, 6, 6)), CashBankId),
            WithBank(Tx(GroceriesCat, 3_000m, 6m, "inflow", new DateOnly(2026, 6, 7)), CashBankId),
            WithBank(Tx(GroceriesCat, 9_000m, 18m, "budgeted", new DateOnly(2026, 6, 8), "bank_account"), BacBankId)
        ], banks: new Dictionary<Guid, string> { [BacBankId] = "BAC", [CashBankId] = "Cash" }).BankMethodBreakdown;

        Assert.Equal(2, rows.Count);
        Assert.Equal(("BAC", "bank_account", 9_000m, 18m), (rows[0].BankName, rows[0].PaymentMethod, rows[0].Actual.Crc, rows[0].Actual.Usd));
        Assert.Equal(("Cash", "credit_card", 12_000m), (rows[1].BankName, rows[1].PaymentMethod, rows[1].Actual.Crc));
        Assert.DoesNotContain(rows, r => r.Actual.Crc == 3_000m); // inflow is income, not spend
    }

    [Fact]
    public void BankMethodBreakdown_HasNoBudgetAxis_ALineAloneMakesNoRow()
    {
        // A plan names no bank, so lines cannot put a row on the bank cut — only money that moved does.
        var rows = With(fixedLines: [FixedLine("Mortgage", 300_000m, 0m, "bank_account", MortgageCat, 1)],
            banks: new Dictionary<Guid, string> { [BacBankId] = "BAC" }).BankMethodBreakdown;
        Assert.Empty(rows);
    }

    [Fact]
    public void BankMethodBreakdown_DeactivatedBankStillNamesItsRow()
    {
        var row = Assert.Single(With(transactions: [WithBank(Tx(DiningCat, 5_000m, 10m, "extraordinary", new DateOnly(2026, 6, 5)), BacBankId)],
            banks: new Dictionary<Guid, string> { [BacBankId] = "BAC (closed)" }).BankMethodBreakdown);
        Assert.Equal(("BAC (closed)", 5_000m), (row.BankName, row.Actual.Crc));
    }

    [Fact]
    public void MethodBreakdown_BudgetsByLineMethod_ActualsByRowMethod_CardThenAccount()
    {
        var rows = With(
            fixedLines: [FixedLine("Mortgage", 300_000m, 0m, "bank_account", MortgageCat, 1), FixedLine("Subscriptions", 10_000m, 0m, "credit_card", DiningCat, 2)],
            transactions:
            [
                WithBank(Tx(DiningCat, 5_000m, 10m, "extraordinary", new DateOnly(2026, 6, 5)), CashBankId),
                WithBank(Tx(GroceriesCat, 3_000m, 6m, "inflow", new DateOnly(2026, 6, 7)), CashBankId),
                WithBank(Tx(GroceriesCat, 9_000m, 18m, "budgeted", new DateOnly(2026, 6, 8), "bank_account"), BacBankId)
            ]).MethodBreakdown;

        Assert.Equal(["credit_card", "bank_account"], rows.Select(r => r.PaymentMethod)); // the order the form offers them
        Assert.Equal((10_000m, 5_000m), (rows[0].Budget.Crc, rows[0].Actual.Crc));
        Assert.Equal((300_000m, 9_000m), (rows[1].Budget.Crc, rows[1].Actual.Crc));
    }

    [Fact]
    public void MethodBreakdown_UsdBudgetConvertsAtRate()
    {
        var rows = With(fixedLines: [FixedLine("Car loan", 0m, 400m, "bank_account", CarLoanCat, 1)]).MethodBreakdown;
        Assert.Equal((400m, 200_000m), (rows[1].Budget.Usd, rows[1].Budget.Crc));
        Assert.Equal((0m, 0m), (rows[0].Budget.Crc, rows[0].Actual.Crc));
    }

    [Fact]
    public void MethodBreakdown_AlwaysTwoRows_EvenWithNothing()
    {
        // The dashboard's Card / Bank account summary is a fixed shape, so an empty month still shows both at zero.
        var s = With();
        Assert.Equal(["credit_card", "bank_account"], s.MethodBreakdown.Select(r => r.PaymentMethod));
        Assert.All(s.MethodBreakdown, r => Assert.Equal((0m, 0m, 0m, 0m), (r.Budget.Crc, r.Budget.Usd, r.Actual.Crc, r.Actual.Usd)));
        Assert.Empty(s.BankMethodBreakdown);
    }
}
