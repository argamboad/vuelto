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
        Assert.Equal((8000m, 16m, Groceries), (groceries.TotalCrc, groceries.TotalUsd, groceries.CategoryId));
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
