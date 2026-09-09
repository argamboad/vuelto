using Vuelto.Core.Budget;
using Vuelto.Core.Entities;

namespace Vuelto.Core.Tests.Budget;

/// <summary>REPORTS-1 (donor US-043 ReportServiceTests, re-homed as a pure calculation): grouping per class, exclusions, sorting, single-month budget decoration.</summary>
public class CategoryAnalysisCalculatorTests
{
    private static readonly Guid Groceries = Guid.NewGuid(), Dining = Guid.NewGuid(), Medical = Guid.NewGuid();
    private static readonly DateOnly From = new(2026, 6, 1), To = new(2026, 6, 30);
    private static readonly Dictionary<Guid, string> Names = new() { [Groceries] = "Groceries", [Dining] = "Dining", [Medical] = "Medical" };

    private static Transaction Tx(Guid category, string type, decimal crc, decimal usd) => new()
    {
        TenantId = Guid.Empty, MonthId = Guid.NewGuid(), BankId = Guid.NewGuid(), CategoryId = category, Payee = "Test",
        OriginalAmount = crc, Currency = "CRC", TransactionDate = new DateOnly(2026, 6, 10), AmountCrc = crc, AmountUsd = usd,
        ExchangeRateUsed = usd == 0 ? 500m : crc / usd, TransactionType = type
    };

    private static FixedExpense Fixed(Guid category, decimal crc, decimal usd, bool active = true) =>
        new() { Name = "Line", CategoryId = category, BudgetCrc = crc, BudgetUsd = usd, IsActive = active };

    private static CategoryAnalysis Run(IReadOnlyList<Transaction> rows, IReadOnlyList<IExpenseLine>? lines = null) =>
        CategoryAnalysisCalculator.Calculate(rows, Names, From, To, lines);

    [Fact]
    public void Groups_BudgetedTransactions_ByCategory()
    {
        var report = Run([Tx(Groceries, "budgeted", 5000m, 10m), Tx(Groceries, "budgeted", 3000m, 6m), Tx(Dining, "budgeted", 2000m, 4m)]);

        Assert.Equal(2, report.Budgeted.Count);
        var groceries = Assert.Single(report.Budgeted, e => e.CategoryName == "Groceries");
        Assert.Equal((8000m, 16m, Groceries, 2), (groceries.TotalCrc, groceries.TotalUsd, groceries.CategoryId, groceries.TransactionCount)); // two rows behind the sum
        Assert.Equal(1, Assert.Single(report.Budgeted, e => e.CategoryName == "Dining").TransactionCount);
        Assert.Single(report.Budgeted, e => e.CategoryName == "Dining");
    }

    [Fact]
    public void Groups_Extraordinary_And_Unplanned_Separately()
    {
        var report = Run([Tx(Dining, "extraordinary", 10000m, 20m), Tx(Medical, "unplanned_essential", 15000m, 30m)]);

        Assert.Empty(report.Budgeted);
        Assert.Single(report.Extraordinary, e => e.CategoryName == "Dining");
        Assert.Single(report.UnplannedEssential, e => e.CategoryName == "Medical");
    }

    [Fact]
    public void Excludes_Inflow_And_EnvelopeContribution()
    {
        var report = Run([Tx(Groceries, "inflow", 50000m, 100m), Tx(Dining, "envelope_contribution", 20000m, 40m)]);
        Assert.Empty(report.Budgeted); Assert.Empty(report.Extraordinary); Assert.Empty(report.UnplannedEssential);
    }

    [Fact]
    public void ZeroSpend_Categories_AreAbsent()
    {
        var report = Run([]);
        Assert.Empty(report.Budgeted); Assert.Empty(report.Extraordinary); Assert.Empty(report.UnplannedEssential);
        Assert.Equal((From, To, false), (report.From, report.To, report.SingleMonth));
    }

    [Fact]
    public void SortsByName_WithinEachClass()
    {
        var report = Run([Tx(Medical, "budgeted", 100m, 0.2m), Tx(Dining, "budgeted", 200m, 0.4m), Tx(Groceries, "budgeted", 300m, 0.6m)]);
        Assert.Equal(["Dining", "Groceries", "Medical"], report.Budgeted.Select(e => e.CategoryName));
    }

    // ---- REPORTS-4: by bank, by payment method, by day ----

    [Fact]
    public void ByCard_GroupsExpenseRows_LargestFirst_NoCardLast_WithAllStatesNames()
    {
        // CARDS-2: the Reports "Spend by card" cut — any period, stored amounts, an inactive card still names its bar.
        var visa = Guid.NewGuid(); var old = Guid.NewGuid();
        Transaction On(Guid? card, string type, decimal crc) { var t = Tx(Groceries, type, crc, crc / 500m); t.CardId = card; return t; }

        var report = CategoryAnalysisCalculator.Calculate(
            [On(visa, "budgeted", 1_000m), On(old, "extraordinary", 4_000m), On(null, "budgeted", 9_000m), On(visa, "inflow", 50_000m)],
            Names, From, To, null, null, new Dictionary<Guid, string> { [visa] = "Allan's Visa", [old] = "Old Amex" });

        Assert.Equal(["Old Amex", "Allan's Visa", ""], report.ByCard.Select(c => c.CardName));
        Assert.Equal((old, 4_000m, 8m, 1), (report.ByCard[0].CardId, report.ByCard[0].TotalCrc, report.ByCard[0].TotalUsd, report.ByCard[0].Count));
        Assert.Null(report.ByCard[2].CardId);
    }

    [Fact]
    public void ByBank_GroupsExpenseRows_LargestFirst_WithAllStatesNames_InflowsExcluded()
    {
        var bac = Guid.NewGuid(); var bn = Guid.NewGuid(); var closed = Guid.NewGuid();
        Transaction At(Guid bank, string type, decimal crc) { var t = Tx(Groceries, type, crc, crc / 500m); t.BankId = bank; return t; }
        var names = new Dictionary<Guid, string> { [bac] = "BAC", [bn] = "BN", [closed] = "Old bank" };

        var report = CategoryAnalysisCalculator.Calculate(
            [At(bn, "budgeted", 1_000m), At(bac, "budgeted", 5_000m), At(bac, "extraordinary", 2_000m), At(closed, "unplanned_essential", 500m), At(bac, "inflow", 90_000m)],
            Names, From, To, null, names);

        Assert.Equal(["BAC", "BN", "Old bank"], report.ByBank.Select(b => b.Label));
        Assert.Equal((bac.ToString(), 7_000m, 14m), (report.ByBank[0].Key, report.ByBank[0].TotalCrc, report.ByBank[0].TotalUsd));
        Assert.Equal(500m, report.ByBank[2].TotalCrc); // a deactivated bank still names its slice
    }

    [Fact]
    public void ByMethod_IsCardThenAccount_OnlyThoseUsed()
    {
        Transaction Via(string method, decimal crc) { var t = Tx(Groceries, "budgeted", crc, crc / 500m); t.PaymentMethod = method; return t; }

        var both = Run([Via("bank_account", 3_000m), Via("credit_card", 1_000m), Via("credit_card", 500m)]);
        Assert.Equal(["credit_card", "bank_account"], both.ByMethod.Select(m => m.Key));
        Assert.Equal((1_500m, 3_000m), (both.ByMethod[0].TotalCrc, both.ByMethod[1].TotalCrc));

        var cardOnly = Run([Via("credit_card", 1_000m)]);
        Assert.Equal("credit_card", Assert.Single(cardOnly.ByMethod).Key);
    }

    [Fact]
    public void ByDay_SumsEachDay_Ascending_SkippingEmptyDays()
    {
        Transaction On(int day, string type, decimal crc) { var t = Tx(Groceries, type, crc, crc / 500m); t.TransactionDate = new DateOnly(2026, 6, day); return t; }

        var report = Run([On(10, "budgeted", 1_000m), On(3, "extraordinary", 200m), On(10, "unplanned_essential", 50m), On(20, "inflow", 9_000m)]);

        Assert.Equal([new DateOnly(2026, 6, 3), new DateOnly(2026, 6, 10)], report.ByDay.Select(d => d.Date));
        Assert.Equal((200m, 1_050m), (report.ByDay[0].TotalCrc, report.ByDay[1].TotalCrc));
    }

    [Fact]
    public void SingleMonth_DecoratesBudgeted_WithTheCatalogBudget()
    {
        var report = Run([Tx(Groceries, "budgeted", 5000m, 10m)], [Fixed(Groceries, 60000m, 120m)]);

        Assert.True(report.SingleMonth);
        var entry = Assert.Single(report.Budgeted);
        Assert.Equal((60000m, 120m), (entry.BudgetedCrc, entry.BudgetedUsd));
    }

    [Fact]
    public void SingleMonth_SumsEveryActiveLine_PerCategory_IgnoringInactive()
    {
        var lines = new List<IExpenseLine>
        {
            Fixed(Groceries, 40000m, 80m),
            new VariableExpense { Name = "Market", CategoryId = Groceries, BudgetCrc = 20000m, BudgetUsd = 40m },
            Fixed(Groceries, 999999m, 0m, active: false)
        };
        var entry = Assert.Single(Run([Tx(Groceries, "budgeted", 5000m, 10m)], lines).Budgeted);
        Assert.Equal((60000m, 120m), (entry.BudgetedCrc, entry.BudgetedUsd));
    }

    [Fact]
    public void SingleMonth_CategoryWithoutALine_HasNullBudget()
    {
        var entry = Assert.Single(Run([Tx(Dining, "budgeted", 5000m, 10m)], [Fixed(Groceries, 60000m, 120m)]).Budgeted);
        Assert.Null(entry.BudgetedCrc); Assert.Null(entry.BudgetedUsd);
    }

    [Fact]
    public void MultiMonth_OmitsBudgetAmounts_EvenWhenLinesExist()
    {
        // The caller passes no lines for a range → no decoration, SingleMonth false.
        var report = Run([Tx(Groceries, "budgeted", 5000m, 10m)]);
        Assert.False(report.SingleMonth);
        var entry = Assert.Single(report.Budgeted);
        Assert.Null(entry.BudgetedCrc); Assert.Null(entry.BudgetedUsd);
    }

    [Fact]
    public void UnknownCategory_GetsAnEmptyName_NotAnException()
    {
        var entry = Assert.Single(Run([Tx(Guid.NewGuid(), "extraordinary", 1m, 0m)]).Extraordinary);
        Assert.Equal("", entry.CategoryName);
    }
}
