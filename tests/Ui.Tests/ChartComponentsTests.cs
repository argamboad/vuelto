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
    public void LineChart_StepsThroughThePoints_DrawsThePlan_AndClampsToday()
    {
        var from = new DateOnly(2026, 6, 25); var to = new DateOnly(2026, 7, 29);
        var points = new List<LinePoint> { new(new DateOnly(2026, 6, 26), 8000m), new(new DateOnly(2026, 7, 3), 78000m) };

        var cut = Render<LineChart>(p => p.Add(x => x.Points, points).Add(x => x.Plan, 150000m).Add(x => x.From, from).Add(x => x.To, to)
            .Add(x => x.Today, new DateOnly(2026, 7, 15)).Add(x => x.TestId, "l"));

        Assert.Equal(2, cut.FindAll("[data-testid='chart-point']").Count);
        Assert.Single(cut.FindAll("[data-testid='chart-plan']"));
        var line = cut.Find("[data-testid='chart-line']");
        Assert.Contains("var(--bs-primary)", line.GetAttribute("stroke")); // under the plan
        var today = cut.Find("[data-testid='chart-today']");
        Assert.Equal(today.GetAttribute("x1"), today.GetAttribute("x2"));
        Assert.Contains("₡150,000", cut.Markup); // the y-axis top is the plan when it is the largest value
        Assert.Contains("Chart_Plan", cut.Find("[data-testid='chart-legend']").TextContent);

        // Past the plan → red; no plan → no plan line and no plan legend; today beyond the period sits on the right edge.
        var over = Render<LineChart>(p => p.Add(x => x.Points, new List<LinePoint> { new(new DateOnly(2026, 7, 3), 200000m) }).Add(x => x.Plan, 150000m)
            .Add(x => x.From, from).Add(x => x.To, to).Add(x => x.Today, new DateOnly(2026, 9, 5)).Add(x => x.TestId, "l"));
        Assert.Contains("var(--bs-danger)", over.Find("[data-testid='chart-line']").GetAttribute("stroke"));
        var noPlan = Render<LineChart>(p => p.Add(x => x.Points, points).Add(x => x.From, from).Add(x => x.To, to).Add(x => x.TestId, "l"));
        Assert.Empty(noPlan.FindAll("[data-testid='chart-plan']"));
        Assert.DoesNotContain("Chart_Plan", noPlan.Find("[data-testid='chart-legend']").TextContent);
        var empty = Render<LineChart>(p => p.Add(x => x.Points, new List<LinePoint>()).Add(x => x.From, from).Add(x => x.To, to).Add(x => x.TestId, "l"));
        Assert.Contains("Chart_Empty", empty.Markup);
    }

    [Fact]
    public void StackedBar_FillsTheTotalInOrder_PaintsTheOverflowRed_AndMarksAPosition()
    {
        var segments = new List<DonutSlice> { new("A", 300m, "red"), new("B", 0m, "blue"), new("C", 500m, "green") };
        var cut = Render<StackedBar>(p => p.Add(x => x.Total, 1000m).Add(x => x.Segments, segments).Add(x => x.Marker, 0.4m).Add(x => x.OverLabel, "Over").Add(x => x.TestId, "b"));

        var drawn = cut.FindAll("[data-testid='chart-segment']");
        Assert.Equal(["A", "C"], drawn.Select(r => r.GetAttribute("data-label") ?? "")); // zero draws nothing
        Assert.Equal("0", drawn[0].GetAttribute("x"));
        Assert.Equal("192", drawn[0].GetAttribute("width"));  // 300 of 1000 across 640
        Assert.Equal("192", drawn[1].GetAttribute("x"));      // C starts where A ends
        Assert.Equal(3, cut.FindAll("[data-testid='chart-legend-item']").Count); // …but is listed
        Assert.Contains("30", cut.FindAll("[data-testid='chart-legend-item']")[0].TextContent); // share of the total
        Assert.Empty(cut.FindAll("[data-testid='chart-overflow']"));
        Assert.Equal("256", cut.Find("[data-testid='chart-marker']").GetAttribute("x1")); // 40 % of the total

        // Past the total: the scale grows to the sum, the excess is red and listed, the marker still sits at its share of the TOTAL.
        var over = Render<StackedBar>(p => p.Add(x => x.Total, 1000m).Add(x => x.Segments, new List<DonutSlice> { new("A", 1250m, "red") }).Add(x => x.Marker, 1m).Add(x => x.OverLabel, "Over").Add(x => x.TestId, "b"));
        Assert.Equal("512", over.Find("[data-testid='chart-overflow']").GetAttribute("x"));    // 1000 of 1250
        Assert.Equal("128", over.Find("[data-testid='chart-overflow']").GetAttribute("width")); // the 250 over
        Assert.Contains("₡250", over.Find("[data-testid='chart-legend-over']").TextContent);
        Assert.Equal("512", over.Find("[data-testid='chart-marker']").GetAttribute("x1"));

        Assert.Contains("Chart_Empty", Render<StackedBar>(p => p.Add(x => x.Total, 0m).Add(x => x.Segments, new List<DonutSlice>()).Add(x => x.TestId, "b")).Markup);
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

        // The hole can say something other than the sum — the reference the ring is measured against (income).
        var referenced = Render<DonutChart>(p => p
            .Add(x => x.Slices, new List<DonutSlice> { new("Spent", 300000m, "red"), new("Remaining", 100000m, "grey") })
            .Add(x => x.CenterText, "₡400,000 income").Add(x => x.TestId, "d"));
        Assert.Equal("₡400,000 income", referenced.Find("[data-testid='chart-center']").TextContent);

        var single = Render<DonutChart>(p => p.Add(x => x.Slices, new List<DonutSlice> { new("Only", 5m, "red") }).Add(x => x.TestId, "d"));
        Assert.Single(single.FindAll("circle[data-testid='chart-slice']")); // a lone slice is a full ring, not a degenerate arc

        var none = Render<DonutChart>(p => p.Add(x => x.Slices, new List<DonutSlice> { new("A", 0m, "red") }).Add(x => x.TestId, "d"));
        Assert.Contains("Chart_Empty", none.Markup);
    }
}
