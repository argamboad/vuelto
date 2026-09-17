using System.Globalization;
using System.Security;
using System.Text;

namespace Vuelto.Api.Features.Reports.Pdf;

/// <summary>One donut slice; <see cref="Color"/> is a literal <c>#RRGGBB</c>.</summary>
public sealed record PdfSlice(string Label, decimal Value, string Color);

/// <summary>One horizontal bar and, optionally, the track it is measured against (same currency).</summary>
public sealed record PdfBar(string Label, decimal Value, decimal? Track);

/// <summary>One point of a cumulative line: the running total as of <see cref="Date"/>.</summary>
public sealed record PdfPoint(DateOnly Date, decimal Value);

/// <summary>
/// REPORTS-7 (ADR-V022): the PDF's charts as SVG strings. The geometry is the web charts' own
/// (<c>Shared.Ui/Components/Charts</c> — donut 150 px with a 22 px ring starting at twelve o'clock; bars on a 640 px
/// row with a 170 px label column; the cumulative step line with a straight plan and a clamped "today" marker), but a
/// PDF has no stylesheet, so every colour is a literal from <see cref="Palette"/> (the light theme's values) and every
/// label is XML-escaped — category, bank and card names are user data. Each builder returns <c>null</c> when there is
/// nothing to draw. The web components are deliberately not shared: the UI library does not reference the API or
/// Core, and the PDF needs no interactivity (see ADR-V022).
/// </summary>
public static class PdfCharts
{
    public const string FontFamily = "Nunito";

    /// <summary>The light theme's colours (Shared.Ui <c>app.css</c> + Bootstrap 5.3 defaults) as literals.</summary>
    public static class Palette
    {
        public const string Primary = "#5A67D8";      // --bs-primary
        public const string AccentLight = "#C5CBF7";  // --brand-accent-light
        public const string Warning = "#FFC107";      // --bs-warning
        public const string Info = "#0DCAF0";         // --bs-info
        public const string Success = "#198754";      // --bs-success (under budget)
        public const string Danger = "#DC3545";       // --bs-danger (over budget)
        public const string Secondary = "#6C757D";    // --bs-secondary
        public const string Tertiary = "#909294";     // --bs-tertiary-color on white
        public const string Rail = "#E9ECEF";         // --bs-secondary-bg
        public const string Ink = "#1B1D33";          // --ink
        public const string Muted = "#6B6F8D";        // --ink-3
        public const string Border = "#E1E3F2";       // --app-border
        public const string Zebra = "#FAFAFE";        // --zebra
        public const string BrandDark = "#3A4178";    // --brand-dark

        /// <summary>The per-bank / per-card series, in the page's order.</summary>
        public static readonly IReadOnlyList<string> Series = [Primary, AccentLight, Warning, Info, Success, Danger, Secondary, Tertiary];
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string N(double v) => v.ToString("0.##", Inv);

    private static string Esc(string text) => SecurityElement.Escape(text) ?? "";

    private static string Open(int width, double height) =>
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {N(height)}\" width=\"{width}\" height=\"{N(height)}\" font-family=\"{FontFamily}\">";

    // ---- donut ----

    private const int DonutSize = 150;
    private const int DonutThickness = 22;
    private const double DonutCenter = DonutSize / 2.0;
    private const double DonutRadius = DonutSize / 2.0 - DonutThickness / 2.0 - 2;

    /// <summary>A ring of the positive slices, twelve o'clock clockwise, with <paramref name="centerText"/> in the hole.</summary>
    public static string? Donut(IReadOnlyList<PdfSlice> slices, string centerText)
    {
        var visible = slices.Where(s => s.Value > 0).ToList();
        var total = visible.Sum(s => s.Value);
        if (total <= 0) return null;

        var sb = new StringBuilder(Open(DonutSize, DonutSize));
        if (visible.Count == 1)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{N(DonutCenter)}\" cy=\"{N(DonutCenter)}\" r=\"{N(DonutRadius)}\" fill=\"none\" stroke=\"{visible[0].Color}\" stroke-width=\"{DonutThickness}\"/>");
        }
        else
        {
            var start = -90.0;
            foreach (var s in visible)
            {
                var sweep = (double)(s.Value / total) * 360.0;
                sb.Append(CultureInfo.InvariantCulture, $"<path d=\"{Arc(start, start + sweep)}\" fill=\"none\" stroke=\"{s.Color}\" stroke-width=\"{DonutThickness}\"/>");
                start += sweep;
            }
        }
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{N(DonutCenter)}\" y=\"{N(DonutCenter + 4.5)}\" text-anchor=\"middle\" fill=\"{Palette.Ink}\" font-size=\"13\" font-weight=\"600\">{Esc(centerText)}</text>");
        return sb.Append("</svg>").ToString();
    }

    private static string Arc(double fromDeg, double toDeg)
    {
        toDeg = Math.Min(toDeg, fromDeg + 359.999); // SVG cannot draw a full circle as one arc
        var (x1, y1) = Point(fromDeg);
        var (x2, y2) = Point(toDeg);
        var large = toDeg - fromDeg > 180 ? 1 : 0;
        return string.Create(Inv, $"M {x1:0.###} {y1:0.###} A {DonutRadius:0.###} {DonutRadius:0.###} 0 {large} 1 {x2:0.###} {y2:0.###}");
    }

    private static (double X, double Y) Point(double deg)
    {
        var rad = deg * Math.PI / 180.0;
        return (Clean(DonutCenter + DonutRadius * Math.Cos(rad)), Clean(DonutCenter + DonutRadius * Math.Sin(rad)));
    }

    /// <summary>Snaps float noise (75.0000000001) so the arc ends print as the round numbers they are.</summary>
    private static double Clean(double v) => Math.Round(v, 6);

    // ---- bars ----

    private const int BarWidth = 640;
    private const int BarLabelWidth = 170;
    private const int BarValueWidth = 130;
    public const int BarTrackWidth = BarWidth - BarLabelWidth - BarValueWidth;
    private const int BarRowHeight = 28;
    private const int BarThickness = 16;
    private const int BarPadding = 6;

    /// <summary>
    /// One row per bar: a rail, the optional track (muted), the value on top — primary, or danger past a positive track.
    /// The longest value or track spans the whole rail.
    /// </summary>
    public static string? Bars(IReadOnlyList<PdfBar> bars, Func<decimal, string> format)
    {
        if (bars.Count == 0) return null;
        var max = Math.Max(bars.Max(b => Math.Max(b.Value, b.Track ?? 0)), 0.01m);
        double Scaled(decimal v) => (double)(BarTrackWidth * Math.Max(v, 0) / max);

        var height = bars.Count * BarRowHeight + BarPadding * 2;
        var sb = new StringBuilder(Open(BarWidth, height));
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            var y = BarPadding + i * BarRowHeight;
            var barY = y + (BarRowHeight - BarThickness) / 2;
            var mid = y + BarRowHeight / 2.0 + 4;
            var over = bar.Track is { } t && t > 0 && bar.Value > t;
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{BarLabelWidth - 8}\" y=\"{N(mid)}\" text-anchor=\"end\" fill=\"{Palette.Ink}\" font-size=\"12\">{Esc(Ellipsis(bar.Label))}</text>");
            sb.Append(CultureInfo.InvariantCulture, $"<rect data-kind=\"rail\" x=\"{BarLabelWidth}\" y=\"{barY}\" width=\"{BarTrackWidth}\" height=\"{BarThickness}\" rx=\"3\" fill=\"{Palette.Rail}\"/>");
            if (bar.Track is { } track && track > 0)
                sb.Append(CultureInfo.InvariantCulture, $"<rect data-kind=\"track\" x=\"{BarLabelWidth}\" y=\"{barY}\" width=\"{N(Scaled(track))}\" height=\"{BarThickness}\" rx=\"3\" fill=\"{Palette.Tertiary}\" fill-opacity=\"0.45\"/>");
            sb.Append(CultureInfo.InvariantCulture, $"<rect data-kind=\"actual\" x=\"{BarLabelWidth}\" y=\"{barY + 2}\" width=\"{N(Scaled(bar.Value))}\" height=\"{BarThickness - 4}\" rx=\"2\" fill=\"{(over ? Palette.Danger : Palette.Primary)}\"/>");
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{BarLabelWidth + BarTrackWidth + 8}\" y=\"{N(mid)}\" fill=\"{Palette.Ink}\" font-size=\"12\">{Esc(format(bar.Value))}</text>");
        }
        return sb.Append("</svg>").ToString();
    }

    private static string Ellipsis(string label) => label.Length <= 22 ? label : label[..21] + "…";

    /// <summary>
    /// A dashed stroke drawn as separate segments of one path. The PDF's SVG engine paints the gaps of a
    /// <c>stroke-dasharray</c> (black stripes), so dashes are geometry here. <c>data-x</c> keeps the start x for tests.
    /// </summary>
    private static string Dashed(string kind, double x1, double y1, double x2, double y2, double dash, double gap, string color, double width)
    {
        var length = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
        var d = new StringBuilder();
        if (length > 0)
        {
            var (ux, uy) = ((x2 - x1) / length, (y2 - y1) / length);
            for (var at = 0.0; at < length; at += dash + gap)
            {
                var end = Math.Min(at + dash, length);
                d.Append(CultureInfo.InvariantCulture, $"M {N(x1 + ux * at)} {N(y1 + uy * at)} L {N(x1 + ux * end)} {N(y1 + uy * end)} ");
            }
        }
        return $"<path data-kind=\"{kind}\" data-x=\"{N(x1)}\" d=\"{d.ToString().TrimEnd()}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"{N(width)}\"/>";
    }

    // ---- line ----

    private const int LineWidth = 640, LineHeight = 200, LineLeft = 70, LineTop = 18, LineRight = 12, LineBottom = 22;
    private const int PlotW = LineWidth - LineLeft - LineRight;
    private const int PlotH = LineHeight - LineTop - LineBottom;

    /// <summary>
    /// The cumulative step line through <paramref name="points"/> (red once the latest total passes a positive plan),
    /// the straight plan from zero to <paramref name="plan"/>, and a dashed "today" marker clamped to the period.
    /// </summary>
    public static string? Line(IReadOnlyList<PdfPoint> points, decimal plan, DateOnly from, DateOnly to, DateOnly today,
        Func<decimal, string> format, Func<DateOnly, string> dateLabel, string todayLabel)
    {
        if (points.Count == 0 && plan <= 0) return null;

        var max = Math.Max(Math.Max(points.Count > 0 ? points.Max(p => p.Value) : 0m, plan), 0.01m);
        var days = Math.Max(to.DayNumber - from.DayNumber, 1);
        DateOnly Clamp(DateOnly d) => d < from ? from : d > to ? to : d;
        string X(DateOnly d) => N(LineLeft + (double)PlotW * (d.DayNumber - from.DayNumber) / days);
        double YValue(decimal v) => LineTop + PlotH * (1 - (double)(Math.Max(v, 0) / max));
        string Y(decimal v) => N(YValue(v));
        var over = plan > 0 && points.Count > 0 && points[^1].Value > plan;
        var ink = over ? Palette.Danger : Palette.Primary;
        var todayX = X(Clamp(today));
        var todayValue = LineLeft + (double)PlotW * (Clamp(today).DayNumber - from.DayNumber) / days;
        var bottom = LineTop + PlotH;

        var sb = new StringBuilder(Open(LineWidth, LineHeight));
        sb.Append(CultureInfo.InvariantCulture, $"<line data-kind=\"axis\" x1=\"{LineLeft}\" y1=\"{LineTop}\" x2=\"{LineLeft}\" y2=\"{bottom}\" stroke=\"{Palette.Border}\"/>");
        sb.Append(CultureInfo.InvariantCulture, $"<line data-kind=\"axis\" x1=\"{LineLeft}\" y1=\"{bottom}\" x2=\"{LineLeft + PlotW}\" y2=\"{bottom}\" stroke=\"{Palette.Border}\"/>");
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{LineLeft - 6}\" y=\"{LineTop + 4}\" text-anchor=\"end\" fill=\"{Palette.Muted}\" font-size=\"11\">{Esc(format(max))}</text>");
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{LineLeft - 6}\" y=\"{bottom + 4}\" text-anchor=\"end\" fill=\"{Palette.Muted}\" font-size=\"11\">0</text>");
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{LineLeft}\" y=\"{LineHeight - 4}\" fill=\"{Palette.Muted}\" font-size=\"11\">{Esc(dateLabel(from))}</text>");
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{LineLeft + PlotW}\" y=\"{LineHeight - 4}\" text-anchor=\"end\" fill=\"{Palette.Muted}\" font-size=\"11\">{Esc(dateLabel(to))}</text>");
        if (plan > 0)
            sb.Append(Dashed("plan", LineLeft, bottom, LineLeft + PlotW, YValue(plan), 6, 4, Palette.AccentLight, 2));
        if (points.Count > 0)
        {
            var parts = new List<string> { $"{X(from)},{Y(0)}" };
            decimal previous = 0;
            foreach (var p in points)
            {
                parts.Add($"{X(p.Date)},{Y(previous)}");
                parts.Add($"{X(p.Date)},{Y(p.Value)}");
                previous = p.Value;
            }
            var last = points[^1];
            parts.Add($"{X(Clamp(today) > last.Date ? Clamp(today) : last.Date)},{Y(last.Value)}");
            sb.Append(CultureInfo.InvariantCulture, $"<polyline points=\"{string.Join(" ", parts)}\" fill=\"none\" stroke=\"{ink}\" stroke-width=\"2.5\" stroke-linejoin=\"round\"/>");
            foreach (var p in points)
                sb.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{X(p.Date)}\" cy=\"{Y(p.Value)}\" r=\"3\" fill=\"{ink}\"/>");
        }
        sb.Append(Dashed("today", todayValue, LineTop, todayValue, bottom, 3, 3, Palette.Warning, 1.5));
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{todayX}\" y=\"{LineTop - 5}\" text-anchor=\"middle\" fill=\"{Palette.Secondary}\" font-size=\"11\">{Esc(todayLabel)}</text>");
        return sb.Append("</svg>").ToString();
    }
}
