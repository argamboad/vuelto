using UglyToad.PdfPig;
using Vuelto.Api.Features.Reports;
using Vuelto.Api.Features.Reports.Pdf;
using Vuelto.Core.Budget;

namespace Vuelto.Api.Tests.Features;

/// <summary>
/// REPORTS-7 layout rules that only show up once the document is paginated: a section heading never sits alone at
/// the bottom of a page with its content on the next one. The month-by-month chart grows with the number of months
/// (up to the report's 12) and a used card adds a row of donuts, so sweeping both moves the "By category" heading
/// across the page.
/// </summary>
public class ReportPdfRendererTests
{
    private static ReportPdfModel Model(int trendMonths, bool withCard, bool twoMethods = true)
    {
        CategorySpendResponse Entry(string name, int i) => new(Guid.CreateVersion7(), name, 10_000m * (i + 1), 20m * (i + 1), 12_000m * (i + 1), 0m, i + 1);
        var analysis = new CategoryAnalysisResponse(
            new ReportPeriodResponse(new DateOnly(2026, 8, 25), new DateOnly(2026, 9, 28)), true,
            Enumerable.Range(0, 8).Select(i => Entry($"Line {i}", i)).ToList(),
            [Entry("Dining", 0)], [Entry("Pharmacy", 0)],
            new ReportMoneyResponse(2_000_000m, 4_000m), new ReportMoneyResponse(500_000m, 1_000m), 500m, 480m,
            twoMethods ? [new("credit_card", "credit_card", 400_000m, 800m), new("bank_account", "bank_account", 100_000m, 200m)] : [new("credit_card", "credit_card", 500_000m, 1_000m)],
            [new("1", "BAC", 300_000m, 600m)],
            [new("credit_card", "credit_card", 250_000m, 500m), new("bank_account", "bank_account", 50_000m, 100m)],
            [new(new DateOnly(2026, 9, 1), 300_000m, 600m)],
            withCard ? [new("a", "Visa", 200_000m, 400m), new("none", "", 100_000m, 200m)] : [new("none", "", 300_000m, 600m)],
            [new("member", Guid.CreateVersion7(), "Allan", new ReportMoneyResponse(1_200_000m, 2_400m)),
             new("household", null, null, new ReportMoneyResponse(500_000m, 1_000m)),
             new("former_member", null, null, new ReportMoneyResponse(200_000m, 400m)),
             new("inflows", null, null, new ReportMoneyResponse(100_000m, 200m))]);
        var trend = new MonthsTrendResponse(Enumerable.Range(0, trendMonths).Select(i =>
            new MonthTrendResponse(Guid.CreateVersion7(), 2024 + i / 12, i % 12 + 1, new ReportMoneyResponse(2_000_000m, 4_000m), new ReportMoneyResponse(1_000_000m, 2_000m))).ToList(), true);
        return ReportPdfModelBuilder.Build(new ReportPdfInput("Casa", DateTimeOffset.UnixEpoch, analysis, new ReportPdfMonth(2026, 9), trend,
            null, null, new ReportPdfOptions("both", "CRC", false, "en", new DateOnly(2026, 9, 16))));
    }

    [Fact]
    public void TheCategoryHeading_AlwaysSharesItsPageWithTheFirstTable()
    {
        foreach (var twoMethods in new[] { true, false })
        foreach (var withCard in new[] { false, true })
        for (var months = 1; months <= ReportHandler.TrendDefaultCount; months++)
        {
            using var pdf = PdfDocument.Open(ReportPdfRenderer.Render(Model(months, withCard, twoMethods)));
            var page = pdf.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text)))
                .Single(t => t.Contains("By category"));
            Assert.True(page.Contains("Category # Budgeted (month) Actual"),
                $"With {months} trend months (card: {withCard}) the \"By category\" heading is separated from its table: {page[^200..]}");

            // INCOME-2: the income heading travels with its table too.
            var income = pdf.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text)))
                .Single(t => t.Contains("Whose Income Share"));
            Assert.True(income.Contains("Income by member Whose Income Share") && income.Contains("Total ₡2,000,000.00 · $4,000.00 100%"),
                $"With {months} trend months (card: {withCard}) the income table is split from its heading or its total.");
        }
    }
}
