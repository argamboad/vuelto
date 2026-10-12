using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Vuelto.Api.Features.Reports.Pdf;

namespace Vuelto.Api.Tests.Features;

/// <summary>
/// REPORTS-7: the PDF's own SVG chart builders. Same geometry as the web charts (Shared.Ui/Components/Charts), but
/// literal colours — a PDF has no stylesheet, so no <c>var(</c> may survive — and every label XML-escaped, because
/// category, bank and card names are user data. Each builder returns null when there is nothing to draw.
/// </summary>
public class ReportPdfChartsTests
{
    private static string Money(decimal v) => "₡" + v.ToString("N0", CultureInfo.InvariantCulture);

    private static XDocument Parse(string svg) => XDocument.Parse(svg); // throws on malformed markup

    private static IEnumerable<XElement> All(XDocument doc, string name) => doc.Descendants().Where(e => e.Name.LocalName == name);

    [Fact]
    public void Donut_DrawsOneArcPerNonZeroSlice_WithLiteralColours()
    {
        var svg = PdfCharts.Donut([new("A", 60m, "#5A67D8"), new("B", 0m, "#C5CBF7"), new("C", 40m, "#FFC107")], "₡100");

        Assert.NotNull(svg);
        var doc = Parse(svg);
        var arcs = All(doc, "path").ToList();
        Assert.Equal(2, arcs.Count);
        Assert.Equal(["#5A67D8", "#FFC107"], arcs.Select(a => (string)a.Attribute("stroke")!));
        Assert.Contains("₡100", All(doc, "text").Select(t => t.Value));
        Assert.DoesNotContain("var(", svg);
    }

    [Fact]
    public void Donut_WithOneVisibleSlice_IsAFullRing()
    {
        var doc = Parse(PdfCharts.Donut([new("A", 5m, "#5A67D8"), new("B", 0m, "#000000")], "x")!);
        Assert.Empty(All(doc, "path"));
        Assert.Equal("#5A67D8", (string)Assert.Single(All(doc, "circle")).Attribute("stroke")!);
    }

    [Fact]
    public void Donut_WithNothingToDraw_IsNull()
    {
        Assert.Null(PdfCharts.Donut([new("A", 0m, "#5A67D8"), new("B", -3m, "#000000")], "x"));
        Assert.Null(PdfCharts.Donut([], "x"));
    }

    [Fact]
    public void Donut_ArcsCoverTheWholeCircle()
    {
        // Two equal halves: the first arc starts at twelve o'clock and the second ends there again.
        var doc = Parse(PdfCharts.Donut([new("A", 1m, "#111111"), new("B", 1m, "#222222")], "x")!);
        var d = All(doc, "path").Select(p => (string)p.Attribute("d")!).ToList();
        Assert.StartsWith("M 75 13 ", d[0]);
        Assert.EndsWith(" 75 13", d[1]);
    }

    [Fact]
    public void Bars_ScaleToTheLongestValueOrTrack_AndTurnRedPastTheTrack()
    {
        var svg = PdfCharts.Bars([new("Groceries", 50m, 100m), new("Fuel", 120m, 100m), new("Gifts", 10m, null)], Money);

        Assert.NotNull(svg);
        var doc = Parse(svg);
        var actual = All(doc, "rect").Where(r => (string?)r.Attribute("data-kind") == "actual").ToList();
        var tracks = All(doc, "rect").Where(r => (string?)r.Attribute("data-kind") == "track").ToList();
        Assert.Equal(3, actual.Count);
        Assert.Equal(2, tracks.Count);

        double Width(XElement r) => double.Parse((string)r.Attribute("width")!, CultureInfo.InvariantCulture);
        Assert.Equal(PdfCharts.BarTrackWidth, Width(actual[1]), 2);           // 120 is the maximum → full track
        Assert.Equal(PdfCharts.BarTrackWidth * 50.0 / 120, Width(actual[0]), 2);
        Assert.Equal(PdfCharts.Palette.Danger, (string)actual[1].Attribute("fill")!); // over its track
        Assert.Equal(PdfCharts.Palette.Primary, (string)actual[0].Attribute("fill")!);
        Assert.Contains("₡120", All(doc, "text").Select(t => t.Value));
        Assert.DoesNotContain("var(", svg);
    }

    [Fact]
    public void Bars_WhenEmpty_IsNull() => Assert.Null(PdfCharts.Bars([], Money));

    [Fact]
    public void Labels_AreXmlEscaped_AndLongOnesShortened()
    {
        var svg = PdfCharts.Bars([new("Tom & Jerry <script>", 1m, null), new("A category name that is far too long", 2m, null)], Money)!;

        var texts = All(Parse(svg), "text").Select(t => t.Value).ToList(); // parses → the markup survived intact
        Assert.Contains("Tom & Jerry <script>", texts);
        Assert.Contains("A category name that …", texts);
        Assert.DoesNotContain("<script>", svg);
    }

    [Fact]
    public void Line_StepsThroughTheDays_WithPlanAndTodayMarker()
    {
        var from = new DateOnly(2026, 6, 25);
        var to = new DateOnly(2026, 7, 29);
        var svg = PdfCharts.Line([new(new(2026, 6, 26), 10m), new(new(2026, 7, 3), 30m)], plan: 100m, from, to, today: new(2026, 7, 15),
            Money, d => d.ToString("d MMM", CultureInfo.InvariantCulture), "Today");

        Assert.NotNull(svg);
        var doc = Parse(svg);
        var line = Assert.Single(All(doc, "polyline"));
        Assert.Equal(PdfCharts.Palette.Primary, (string)line.Attribute("stroke")!);
        // start at zero, then two rises (each a hold + a step), then held to today
        Assert.Equal(6, ((string)line.Attribute("points")!).Split(' ').Length);
        Assert.Equal(2, All(doc, "circle").Count());
        // Dashes are drawn as segments (the PDF's SVG engine paints dash-array gaps), so no dasharray survives.
        Assert.Contains(All(doc, "path"), l => (string?)l.Attribute("data-kind") == "plan");
        Assert.Contains(All(doc, "path"), l => (string?)l.Attribute("data-kind") == "today");
        Assert.DoesNotContain("dasharray", svg);
        Assert.Contains("Today", All(doc, "text").Select(t => t.Value));
        Assert.DoesNotContain("var(", svg);
    }

    [Fact]
    public void Line_TurnsRed_WhenSpendIsPastThePlan_AndDrawsNoPlanWithoutOne()
    {
        var from = new DateOnly(2026, 6, 1);
        var to = new DateOnly(2026, 6, 30);
        var over = Parse(PdfCharts.Line([new(new(2026, 6, 2), 150m)], 100m, from, to, new(2026, 6, 3), Money, d => "", "Today")!);
        Assert.Equal(PdfCharts.Palette.Danger, (string)Assert.Single(All(over, "polyline")).Attribute("stroke")!);

        var noPlan = Parse(PdfCharts.Line([new(new(2026, 6, 2), 150m)], 0m, from, to, new(2026, 6, 3), Money, d => "", "Today")!);
        Assert.DoesNotContain(All(noPlan, "path"), l => (string?)l.Attribute("data-kind") == "plan");
    }

    [Fact]
    public void Line_ClampsTodayToThePeriod()
    {
        var from = new DateOnly(2026, 6, 1);
        var to = new DateOnly(2026, 6, 30);
        string TodayX(DateOnly today) => (string)All(Parse(PdfCharts.Line([new(from, 1m)], 0m, from, to, today, Money, d => "", "T")!), "path")
            .Single(l => (string?)l.Attribute("data-kind") == "today").Attribute("data-x")!;

        Assert.Equal(TodayX(to), TodayX(new DateOnly(2026, 8, 1)));
        Assert.Equal(TodayX(from), TodayX(new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void Line_WithNoPointsAndNoPlan_IsNull() =>
        Assert.Null(PdfCharts.Line([], 0m, new(2026, 6, 1), new(2026, 6, 30), new(2026, 6, 3), Money, d => "", "T"));

    [Fact]
    public void EveryColourIsALiteralHex()
    {
        var svg = PdfCharts.Donut([new("A", 1m, PdfCharts.Palette.Series[0]), new("B", 1m, PdfCharts.Palette.Series[1])], "x")
                  + PdfCharts.Bars([new("A", 1m, 2m)], Money)
                  + PdfCharts.Line([new(new(2026, 6, 2), 1m)], 2m, new(2026, 6, 1), new(2026, 6, 30), new(2026, 6, 3), Money, d => "", "T");
        foreach (Match m in Regex.Matches(svg, "(?:fill|stroke)=\"([^\"]*)\""))
            Assert.Matches("^(#[0-9A-F]{6}|none)$", m.Groups[1].Value);
    }
}
