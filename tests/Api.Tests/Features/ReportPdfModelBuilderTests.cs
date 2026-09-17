using Vuelto.Api.Features.Reports;
using Vuelto.Api.Features.Reports.Pdf;
using Vuelto.Core.Budget;

namespace Vuelto.Api.Tests.Features;

/// <summary>
/// REPORTS-7: the pure step that turns the report the page shows (category analysis, months trend, pending refunds,
/// the CSV rows) into a laid-out-ready model — every string already localized and formatted, every chart already an
/// SVG. Mirrors the Reports page rule for rule: the four tiles and their subtitles, pace only for a month, income and
/// budget cards that say why when there is no rate, the category tables (budget + red/green only for the budgeted
/// class of a month), and the appendix that follows the "show in" preference.
/// </summary>
public class ReportPdfModelBuilderTests
{
    private static readonly DateTimeOffset Generated = new(2026, 9, 3, 12, 30, 0, TimeSpan.Zero);
    private static readonly Guid BacId = Guid.CreateVersion7();

    private static CategoryAnalysisResponse June(bool withRate = true, bool withCard = false, bool empty = false) => new(
        new ReportPeriodResponse(new DateOnly(2026, 5, 28), new DateOnly(2026, 6, 24)),
        SingleMonth: true,
        Budgeted: empty ? [] :
        [
            new(Guid.CreateVersion7(), "Groceries", 48_000m, 96m, 60_000m, 0m, 3),
            new(Guid.CreateVersion7(), "Streaming", 6_000m, 12m, 0m, 10m, 1),
        ],
        Extraordinary: empty ? [] : [new(Guid.CreateVersion7(), "Dining", 15_750m, 31.5m, null, null, 2)],
        UnplannedEssential: empty ? [] : [new(Guid.CreateVersion7(), "Pharmacy", 5_000m, 10m, null, null, 1)],
        Income: withRate ? new ReportMoneyResponse(1_500_000m, 3_000m) : null,
        BudgetTotal: withRate ? new ReportMoneyResponse(65_000m, 135m) : null,
        ExchangeRate: withRate ? 500m : null,
        ExchangeRateBuy: withRate ? 480m : null,
        BudgetByMethod: withRate ? [new("credit_card", "credit_card", 65_000m, 135m)] : null,
        ByBank: empty ? [] : [new(BacId.ToString(), "BAC", 70_000m, 140m), new(Guid.CreateVersion7().ToString(), "", 4_750m, 9.5m)],
        ByMethod: empty ? [] : [new("credit_card", "credit_card", 69_750m, 139.5m), new("bank_account", "bank_account", 5_000m, 10m)],
        SpendByDay: empty ? [] : [new(new DateOnly(2026, 5, 30), 48_000m, 96m), new(new DateOnly(2026, 6, 10), 26_750m, 53.5m)],
        ByCard: withCard
            ? [new(Guid.CreateVersion7().ToString(), "Allan's Visa", 60_000m, 120m), new("none", "", 14_750m, 29.5m)]
            : [new("none", "", 74_750m, 149.5m)],
        IncomeByMember: withRate
            ?
            [
                NewSlice("member", Guid.CreateVersion7(), "Allan", 1_000_000m, 2_000m),
                NewSlice("household", null, null, 375_000m, 750m),
                NewSlice("former_member", null, null, 100_000m, 200m),
                NewSlice("inflows", null, null, 25_000m, 50m),
            ]
            : null);

    private static IncomeMemberResponse NewSlice(string kind, Guid? id, string? name, decimal crc, decimal usd) =>
        new(kind, id, name, new ReportMoneyResponse(crc, usd));

    private static CategoryAnalysisResponse Range() => June() with
    {
        Period = new ReportPeriodResponse(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30)),
        SingleMonth = false,
        Budgeted = [new(Guid.CreateVersion7(), "Groceries", 48_000m, 96m, null, null, 3)],
        Income = null, BudgetTotal = null, ExchangeRate = null, ExchangeRateBuy = null, BudgetByMethod = null, SpendByDay = null,
        IncomeByMember = null,
    };

    private static MonthsTrendResponse Trend(bool rate = true) => new(
    [
        new(Guid.CreateVersion7(), 2026, 5, rate ? new ReportMoneyResponse(1_500_000m, 3_000m) : null, new ReportMoneyResponse(1_600_000m, 3_200m)),
        new(Guid.CreateVersion7(), 2026, 6, rate ? new ReportMoneyResponse(1_500_000m, 3_000m) : null, new ReportMoneyResponse(74_750m, 149.5m)),
    ], rate);

    private static readonly TransactionExportRow[] Rows =
    [
        new(new DateOnly(2026, 6, 10), "Soda Tapia", "Dining", "extraordinary", 15_750m, 31.5m, 500m, "credit_card", "BAC", "email", "Allan's Visa", "birthday"),
        new(new DateOnly(2026, 5, 30), "Walmart", "Groceries", "budgeted", 48_000m, 96m, 500m, "bank_account", null, "manual"),
    ];

    private static ReportPdfModel Build(
        CategoryAnalysisResponse? analysis = null, string display = "both", string chart = "CRC", string language = "en",
        bool appendix = true, TransactionExportRow[]? rows = null, MonthsTrendResponse? trend = null, MoneyPair? refunds = null,
        DateOnly? today = null, bool month = true)
    {
        var a = analysis ?? June();
        return ReportPdfModelBuilder.Build(new ReportPdfInput(
            "Casa Gamboa", Generated, a,
            month && a.SingleMonth ? new ReportPdfMonth(2026, 6) : null,
            a.SingleMonth ? trend ?? Trend() : null,
            refunds,
            appendix ? rows ?? Rows : null,
            new ReportPdfOptions(display, chart, appendix, language, today ?? new DateOnly(2026, 6, 10))));
    }

    // ---- header ----

    [Fact]
    public void Header_NamesTheHousehold_TheMonth_ThePeriod_AndTheRatesUsed()
    {
        var h = Build().Header;
        Assert.Equal("Casa Gamboa", h.Household);
        Assert.Equal("June 2026", h.Heading);
        Assert.Equal("28 May 2026 – 24 Jun 2026", h.Period);
        Assert.Equal("one budget month — budgets shown next to actuals", h.Scope);
        Assert.Equal("Generated 3 Sep 2026 12:30 UTC", h.Generated);
        Assert.Equal("Exchange rate: buy ₡480.00 · sell ₡500.00 per $1", h.Rates);
    }

    [Fact]
    public void Header_InSpanish_AndForARange()
    {
        var es = Build(language: "es").Header;
        Assert.Equal("Junio 2026", es.Heading);
        Assert.Equal("Informe de gastos", es.Title);

        var range = Build(Range()).Header;
        Assert.Equal("1 Jun 2026 – 30 Jun 2026", range.Heading);
        Assert.Equal("custom range — monthly budgets and income don't apply", range.Scope);
        Assert.Null(range.Rates);
    }

    [Fact]
    public void Header_SaysWhenThereIsNoRate()
    {
        Assert.Equal("Today's exchange rate isn't available — income and budget figures are left out.", Build(June(withRate: false)).Header.Rates);
    }

    // ---- tiles ----

    [Fact]
    public void Kpis_MatchThePageTiles_InBothCurrencies()
    {
        var k = Build(refunds: new MoneyPair(2_500m, 5m)).Kpis;
        Assert.Equal(["Total spend", "Budgeted", "Discretionary", "Unplanned"], k.Select(x => x.Label));
        Assert.Equal("₡74,750.00 · $149.50", k[0].Value);
        Assert.Equal("5% of income", k[0].Sub);
        Assert.Equal("₡54,000.00 · $108.00", k[1].Value);
        Assert.Equal("72% of spend", k[1].Sub);
        Assert.Equal("21% of spend", k[2].Sub);
        Assert.Equal("₡2,500.00 refundable", k[3].Sub); // "both" shows the refundable figure in colones
    }

    [Fact]
    public void Kpis_FollowTheShowInPreference_AndTheLanguage()
    {
        var k = Build(display: "USD", language: "es").Kpis;
        Assert.Equal("Gasto total", k[0].Label);
        Assert.Equal("$149,50", k[0].Value);
        Assert.Equal("5% del ingreso", k[0].Sub);
        Assert.Equal("7% del gasto", k[3].Sub); // no refunds → the share
    }

    [Fact]
    public void Kpis_ForARange_HaveNoIncomeShare() => Assert.Null(Build(Range()).Kpis[0].Sub);

    // ---- pace ----

    [Fact]
    public void Pace_IsAMonthPicture_WithTheCaptionAndThePlan()
    {
        var pace = Build(today: new DateOnly(2026, 6, 10)).Pace;
        Assert.NotNull(pace);
        Assert.Equal("Pace", pace.Title);
        Assert.Equal("50% of the month elapsed · 115% of the plan spent", pace.Caption); // 14 of 28 days; 74,750 of 65,000
        Assert.NotNull(pace.Svg);
        Assert.Contains("data-kind=\"plan\"", pace.Svg);
        Assert.Empty(pace.Notes);

        Assert.Null(Build(Range()).Pace);
    }

    [Fact]
    public void Pace_WithoutARate_DrawsTheSpendOnly_AndSaysWhy()
    {
        var pace = Build(June(withRate: false)).Pace!;
        Assert.Null(pace.Caption);
        Assert.DoesNotContain("data-kind=\"plan\"", pace.Svg);
        Assert.Equal("The plan line needs today's exchange rate, which isn't available right now.", Assert.Single(pace.Notes).Text);
    }

    [Fact]
    public void Pace_DrawsInTheShowInCurrency_OrTheChartCurrencyForBoth()
    {
        Assert.Contains("$", Build(display: "USD").Pace!.Svg);
        Assert.Contains("₡", Build(display: "both", chart: "CRC").Pace!.Svg);
        Assert.Contains("$", Build(display: "both", chart: "USD").Pace!.Svg);
    }

    // ---- charts ----

    [Fact]
    public void Charts_ForAMonth_AndForARange()
    {
        Assert.Equal(["class", "income", "budget", "members", "trend", "bank", "method", "method-budget"], Build().Charts.Select(c => c.Key));
        Assert.Equal(["class", "income", "budget", "members", "trend", "bank", "card", "method", "method-budget"], Build(June(withCard: true)).Charts.Select(c => c.Key));
        Assert.Equal(["class", "bank", "method"], Build(Range()).Charts.Select(c => c.Key));
    }

    [Fact]
    public void ClassChart_LegendCarriesAmountAndShare()
    {
        var chart = Build().Charts.Single(c => c.Key == "class");
        Assert.Equal("Spend by class", chart.Title);
        Assert.Equal(["Budgeted", "Discretionary", "Unplanned"], chart.Legend.Select(l => l.Label));
        Assert.Equal("₡54,000 · 72%", chart.Legend[0].Value);
        Assert.Equal(PdfCharts.Palette.Primary, chart.Legend[0].Color);
        Assert.NotNull(chart.Svg);
    }

    [Fact]
    public void IncomeAndBudgetCharts_MeasureAgainstTheIncome_AndFlagOverruns()
    {
        var charts = Build(chart: "USD").Charts;
        var income = charts.Single(c => c.Key == "income");
        Assert.Equal(["Budgeted", "Discretionary", "Unplanned", "Remaining"], income.Legend.Select(l => l.Label));
        Assert.Contains(">$3,000<", income.Svg);
        Assert.Empty(income.Notes);

        var budget = charts.Single(c => c.Key == "budget");
        Assert.Equal(["Budget lines", "Uncommitted"], budget.Legend.Select(l => l.Label));

        var tight = June() with { Income = new ReportMoneyResponse(50_000m, 100m) };
        var tightCharts = Build(tight).Charts;
        Assert.Equal(new PdfNote("Over income by ₡24,750.00", Alert: true), Assert.Single(tightCharts.Single(c => c.Key == "income").Notes));
        Assert.Equal(new PdfNote("Budget exceeds income by ₡15,000.00", Alert: true), Assert.Single(tightCharts.Single(c => c.Key == "budget").Notes));
    }

    [Fact]
    public void IncomeAndBudgetCharts_WithoutARate_SayWhy()
    {
        var charts = Build(June(withRate: false)).Charts;
        foreach (var key in new[] { "income", "budget" })
        {
            var c = charts.Single(x => x.Key == key);
            Assert.Null(c.Svg);
            Assert.Equal("Income needs today's exchange rate, which isn't available right now.", Assert.Single(c.Notes).Text);
        }
        Assert.DoesNotContain(charts, c => c.Key == "method-budget");
    }

    [Fact]
    public void IncomeByMember_IsADonutAndATable_WhoseSharesAddUp()
    {
        var model = Build();
        var donut = model.Charts.Single(c => c.Key == "members");
        Assert.Equal(["Allan", "The household", "Former members", "Other income (inflows)"], donut.Legend.Select(l => l.Label));
        Assert.Equal("₡1,000,000 · 67%", donut.Legend[0].Value);

        var table = model.Income!;
        Assert.Equal("Income by member", table.Title);
        Assert.Equal(["Whose", "Income", "Share"], table.Columns.Select(c => c.Header));
        Assert.Equal(
            [["Allan", "₡1,000,000.00 · $2,000.00", "67%"], ["The household", "₡375,000.00 · $750.00", "25%"],
             ["Former members", "₡100,000.00 · $200.00", "7%"], ["Other income (inflows)", "₡25,000.00 · $50.00", "2%"]],
            table.Rows.Select(r => r.Select(c => c.Text).ToArray()));
        Assert.Equal(["Total", "₡1,500,000.00 · $3,000.00", "100%"], table.Total!.Select(c => c.Text));

        // Shares follow the "show in" side; Spanish labels.
        var usd = Build(display: "USD", language: "es").Income!;
        Assert.Equal("Ingreso por miembro", usd.Title);
        Assert.Equal(["Allan", "$2.000,00", "67%"], usd.Rows[0].Select(c => c.Text));
        Assert.Equal("El hogar", usd.Rows[1][0].Text);
    }

    [Fact]
    public void IncomeByMember_IsLeftOut_ForARange_WithoutARate_AndSaysSoWhenTheMonthHasNoIncome()
    {
        Assert.Null(Build(Range()).Income);
        Assert.Null(Build(June(withRate: false)).Income);
        Assert.DoesNotContain(Build(June(withRate: false)).Charts, c => c.Key == "members");

        var none = Build(June() with { Income = new ReportMoneyResponse(0m, 0m), IncomeByMember = [] });
        Assert.Equal("No income recorded for this month.", none.Income!.EmptyNote);
        Assert.Null(none.Income.Total);
        Assert.DoesNotContain(none.Charts, c => c.Key == "members");

        var formerNamed = Build(June() with { IncomeByMember = [NewSlice("member", Guid.CreateVersion7(), null, 1m, 1m)] });
        Assert.Equal("Former members", formerNamed.Income!.Rows[0][0].Text); // a member without a name never prints blank
    }

    [Fact]
    public void TrendChart_PutsSpendOnTheIncomeTrack_AndSaysWhenThereIsNoRate()
    {
        var trend = Build().Charts.Single(c => c.Key == "trend");
        Assert.Contains(">May 2026<", trend.Svg);
        Assert.Equal(["Spend", "Income", "Spent more than the income"], trend.Legend.Select(l => l.Label));

        var noRate = Build(trend: Trend(rate: false)).Charts.Single(c => c.Key == "trend");
        Assert.Equal("Income tracks need today's exchange rate, which isn't available right now — the bars show spend only.", Assert.Single(noRate.Notes).Text);
    }

    [Fact]
    public void BankCardAndMethodCharts_NameTheirBuckets()
    {
        var charts = Build(June(withCard: true)).Charts;
        Assert.Equal(["BAC", "Unknown bank"], charts.Single(c => c.Key == "bank").Legend.Select(l => l.Label));
        Assert.Equal(["Allan's Visa", "No card"], charts.Single(c => c.Key == "card").Legend.Select(l => l.Label));
        Assert.Equal(["Credit card", "Bank account"], charts.Single(c => c.Key == "method").Legend.Select(l => l.Label));

        var methodBudget = charts.Single(c => c.Key == "method-budget");
        Assert.Equal("Budgeted vs spent, by payment method", methodBudget.Title);
        Assert.Equal("Credit card: budgeted ₡65,000.00 · spent ₡69,750.00", Assert.Single(methodBudget.Notes).Text);
    }

    // ---- category tables ----

    [Fact]
    public void Categories_BudgetedClassOfAMonth_ShowsBudgets_AndJudgesEachLineInItsOwnCurrency()
    {
        var budgeted = Build().Categories[0];
        Assert.Equal("Budgeted", budgeted.Title);
        Assert.Equal(["Category", "#", "Budgeted (month)", "Actual"], budgeted.Columns.Select(c => c.Header));

        var groceries = budgeted.Rows[0];
        Assert.Equal(["Groceries", "3", "₡60,000.00", "₡48,000.00 · $96.00"], groceries.Select(c => c.Text));
        Assert.Equal(PdfCharts.Palette.Success, groceries[3].Color);

        var streaming = budgeted.Rows[1];
        Assert.Equal("$10.00", streaming[2].Text);
        Assert.Equal(PdfCharts.Palette.Danger, streaming[3].Color); // $12 of $10, although its colón side has no budget

        // The rows' budgets as one figure: dollars at sell, colones at buy.
        Assert.Equal(["Total", "4", "₡65,000.00 · $135.00", "₡54,000.00 · $108.00"], budgeted.Total!.Select(c => c.Text));
    }

    [Fact]
    public void Categories_OtherClasses_AndRanges_ShowSpendOnly()
    {
        var tables = Build().Categories;
        Assert.Equal(["Budgeted", "Discretionary", "Unplanned"], tables.Select(t => t.Title));
        Assert.Equal(["Category", "#", "Spent"], tables[1].Columns.Select(c => c.Header));
        Assert.Null(tables[1].Rows[0][2].Color);

        var range = Build(Range()).Categories[0];
        Assert.Equal(["Category", "#", "Spent"], range.Columns.Select(c => c.Header));
    }

    [Fact]
    public void Categories_AnEmptyClass_SaysSo()
    {
        var a = June() with { UnplannedEssential = [] };
        var unplanned = Build(a).Categories[2];
        Assert.Empty(unplanned.Rows);
        Assert.Null(unplanned.Total);
        Assert.Equal("Nothing in this class for the period.", unplanned.EmptyNote);
    }

    [Fact]
    public void AnEmptyPeriod_KeepsTheTiles_AndSaysThereIsNoSpending()
    {
        var model = Build(June(empty: true), rows: []);
        Assert.Equal("No spending in this period.", model.EmptyNote);
        Assert.Empty(model.Charts);
        Assert.Empty(model.Categories);
        Assert.Equal(4, model.Kpis.Count);
        Assert.Null(Build().EmptyNote);
    }

    // ---- appendix ----

    [Fact]
    public void Appendix_HasEveryCsvColumn_InTheCsvOrder_WithReadableLabels()
    {
        var appendix = Build().Appendix!;
        Assert.Equal("Transactions", appendix.Title);
        Assert.Equal(["Date", "Payee", "Category", "Class", "₡", "$", "Rate", "Method", "Bank", "Source", "Card", "Notes"], appendix.Columns.Select(c => c.Header));
        Assert.Equal(["6/10/2026", "Soda Tapia", "Dining", "Discretionary", "₡15,750.00", "$31.50", "500.00", "Credit card", "BAC", "Email", "Allan's Visa", "birthday"],
            appendix.Rows[0].Select(c => c.Text));
        Assert.Equal(["5/30/2026", "Walmart", "Groceries", "Budgeted", "₡48,000.00", "$96.00", "500.00", "Bank account", "", "Manual", "", ""],
            appendix.Rows[1].Select(c => c.Text));
        Assert.Null(appendix.Total);
    }

    [Fact]
    public void Appendix_FollowsTheShowInPreference_AndTheLanguage()
    {
        var appendix = Build(display: "CRC", language: "es").Appendix!;
        Assert.Equal("Transacciones", appendix.Title);
        Assert.DoesNotContain("$", appendix.Columns.Select(c => c.Header));
        Assert.Equal(["10/6/2026", "Soda Tapia", "Dining", "Discrecional", "₡15.750,00", "500,00", "Tarjeta de crédito", "BAC", "Correo", "Allan's Visa", "birthday"],
            appendix.Rows[0].Select(c => c.Text));
    }

    [Fact]
    public void Appendix_IsLeftOut_WhenNotAsked_AndSaysSoWhenEmpty()
    {
        Assert.Null(Build(appendix: false).Appendix);
        var empty = Build(rows: []).Appendix!;
        Assert.Empty(empty.Rows);
        Assert.Equal("No transactions in this period.", empty.EmptyNote);
    }

    [Fact]
    public void Appendix_LabelsTheOtherClassesAndSources()
    {
        TransactionExportRow[] rows =
        [
            new(new DateOnly(2026, 6, 1), "Refund", "Health", "inflow", 1m, 0m, 500m, "bank_account", "BAC", "refund_realization"),
            new(new DateOnly(2026, 6, 2), "Savings", "Savings", "envelope_contribution", 1m, 0m, 500m, "bank_account", "BAC", "manual"),
            new(new DateOnly(2026, 6, 3), "Odd", null, "unplanned_essential", 1m, 0m, 500m, "something_else", "BAC", "future_source"),
        ];
        var texts = Build(rows: rows).Appendix!.Rows.Select(r => r.Select(c => c.Text).ToList()).ToList();
        Assert.Equal(("Income (inflow)", "Refund"), (texts[0][3], texts[0][9]));
        Assert.Equal("Envelope contribution", texts[1][3]);
        Assert.Equal(("", "Unplanned", "something_else", "future_source"), (texts[2][2], texts[2][3], texts[2][7], texts[2][9])); // unknown codes pass through
    }

    [Fact]
    public void Footer_IsLocalized()
    {
        Assert.Equal(new ReportPdfFooter("¿Y el vuelto?", "Page", "of"), Build().Footer);
        Assert.Equal(new ReportPdfFooter("¿Y el vuelto?", "Página", "de"), Build(language: "es").Footer);
    }
}
