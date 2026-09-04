using Bunit;
using Vuelto.Shared.Ui.Components.Charts;
using Vuelto.Ui.Tests.Infrastructure;
using Xunit;

namespace Vuelto.Ui.Tests;

/// <summary>The inline-SVG charts: bars scale to the longest value, a budget overlay marks over-budget in red, zero input says so; the donut draws one arc per non-zero slice with a legend carrying share and amount.</summary>
public class ChartComponentsTests : ComponentTestBase
{
    [Fact]
    public void BarChart_ScalesToTheLongestBar_AndMarksOverBudget()
    {
        var cut = Render<BarChart>(p => p
            .Add(x => x.Items, new List<BarItem> { new("Groceries", 8000m, 60000m), new("Housing", 70000m, 60000m), new("Other", 100m) })
            .Add(x => x.Currency, "CRC").Add(x => x.TestId, "t"));

        var bars = cut.FindAll("[data-testid='chart-bar']");
        Assert.Equal(3, bars.Count);
        Assert.Equal("false", bars[0].GetAttribute("data-over"));
        Assert.Equal("true", bars[1].GetAttribute("data-over")); // 70,000 past a 60,000 budget
        Assert.Equal("false", bars[2].GetAttribute("data-over")); // no budget → never "over"
        Assert.Contains("--bs-danger", bars[1].QuerySelector("[data-testid='chart-actual']")!.GetAttribute("fill"));
        Assert.Contains("--bs-primary", bars[0].QuerySelector("[data-testid='chart-actual']")!.GetAttribute("fill"));

        // The longest bar (Housing, 70,000) spans the whole track; Groceries is 8/70 of it.
        var track = double.Parse(bars[1].QuerySelector("[data-testid='chart-actual']")!.GetAttribute("width")!, System.Globalization.CultureInfo.InvariantCulture);
        var groceries = double.Parse(bars[0].QuerySelector("[data-testid='chart-actual']")!.GetAttribute("width")!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(8000.0 / 70000.0, groceries / track, 3);
        Assert.Equal(2, cut.FindAll("[data-testid='chart-budget']").Count); // only budgeted rows get the overlay
        Assert.Contains("₡8,000.00", bars[0].QuerySelector("[data-testid='chart-value']")!.TextContent);
        Assert.NotNull(cut.Find("[data-testid='chart-legend']"));
    }

    [Fact]
    public void BarChart_WithNoRows_SaysSo_AndUsdFormatsWithDollars()
    {
        var empty = Render<BarChart>(p => p.Add(x => x.Items, new List<BarItem>()).Add(x => x.TestId, "t"));
        Assert.Contains("Chart_Empty", empty.Markup);
        Assert.Empty(empty.FindAll("svg"));

        var usd = Render<BarChart>(p => p.Add(x => x.Items, new List<BarItem> { new("Netflix", 17.99m) }).Add(x => x.Currency, "USD").Add(x => x.TestId, "t"));
        Assert.Contains("$17.99", usd.Find("[data-testid='chart-value']").TextContent);
        Assert.Empty(usd.FindAll("[data-testid='chart-legend']")); // no budgets → no legend
    }

    [Fact]
    public void DonutChart_DrawsOneArcPerNonZeroSlice_WithSharesInTheLegend()
    {
        var cut = Render<DonutChart>(p => p
            .Add(x => x.Slices, new List<DonutSlice> { new("Budgeted", 300000m, "var(--bs-primary)"), new("Discretionary", 100000m, "var(--brand-accent-light)"), new("Unplanned", 0m, "var(--bs-warning)") })
            .Add(x => x.TestId, "d"));

        Assert.Equal(2, cut.FindAll("[data-testid='chart-slice']").Count); // the zero slice draws nothing
        var legend = cut.FindAll("[data-testid='chart-legend-item']");
        Assert.Equal(3, legend.Count); // …but is still listed
        Assert.Contains("75", legend[0].TextContent); // 300k of 400k
        Assert.Contains("25", legend[1].TextContent);
        Assert.Contains("₡400,000", cut.Find("svg text").TextContent); // the total in the hole

        var single = Render<DonutChart>(p => p.Add(x => x.Slices, new List<DonutSlice> { new("Only", 5m, "red") }).Add(x => x.TestId, "d"));
        Assert.Single(single.FindAll("circle[data-testid='chart-slice']")); // a lone slice is a full ring, not a degenerate arc

        var none = Render<DonutChart>(p => p.Add(x => x.Slices, new List<DonutSlice> { new("A", 0m, "red") }).Add(x => x.TestId, "d"));
        Assert.Contains("Chart_Empty", none.Markup);
    }
}
