using System.Globalization;
using System.Text.RegularExpressions;
using QuestPDF;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using P = Vuelto.Api.Features.Reports.Pdf.PdfCharts.Palette;

namespace Vuelto.Api.Features.Reports.Pdf;

/// <summary>
/// REPORTS-7 (ADR-V022): lays a <see cref="ReportPdfModel"/> out with QuestPDF. Letter portrait for the report, Letter
/// landscape for the transaction appendix (it carries every CSV column). Only arrangement lives here — every figure
/// and label arrives formatted — so this class is exercised end to end (the PDF's text read back) rather than unit by
/// unit. The embedded Nunito faces are the only fonts it may use (system fonts are off, so a server and a laptop
/// produce the same file); a glyph Nunito lacks — an emoji in a payee — falls back to QuestPDF's bundled face instead
/// of failing the whole report.
/// </summary>
public static partial class ReportPdfRenderer
{
    private static readonly Lazy<string> Lockup = new(Initialize);

    /// <summary>Licence, font policy and the embedded faces — once per process.</summary>
    private static string Initialize()
    {
        Settings.License = LicenseType.Community; // free under USD 1M annual revenue (ADR-V022)
        Settings.UseSystemFonts = false;
        Settings.ThrowOnMissingTextGlyphs = false;
        var assembly = typeof(ReportPdfRenderer).Assembly;
        foreach (var face in new[] { "Regular", "SemiBold", "Bold" })
        {
            using var stream = assembly.GetManifestResourceStream($"Vuelto.Reports.Pdf.Nunito-{face}.ttf")
                ?? throw new InvalidOperationException($"Embedded font Nunito-{face} is missing.");
            FontManager.RegisterFontFromStream(stream);
        }
        using var logo = assembly.GetManifestResourceStream("Vuelto.Reports.Pdf.lockup_light.svg")
            ?? throw new InvalidOperationException("Embedded brand lockup is missing.");
        using var reader = new StreamReader(logo);
        return reader.ReadToEnd();
    }

    public static byte[] Render(ReportPdfModel model) => Compose(model).GeneratePdf();

    /// <summary>The composed document, unrendered — the PDF above, or page images when walking the layout (QA).</summary>
    public static IDocument Compose(ReportPdfModel model)
    {
        var lockup = Lockup.Value;
        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                Setup(page, PageSizes.Letter, model, lockup);
                page.Content().PaddingTop(10).Column(col => Report(col, model));
            });
            if (model.Appendix is { } appendix)
            {
                doc.Page(page =>
                {
                    Setup(page, PageSizes.Letter.Landscape(), model, lockup);
                    page.Content().PaddingTop(10).Column(col =>
                    {
                        col.Item().Text(appendix.Title).FontSize(14).SemiBold().FontColor(P.BrandDark);
                        col.Item().PaddingTop(6).Element(c => Table(c, appendix, fontSize: 7.5f, repeatHeader: true));
                    });
                });
            }
        })
        .WithMetadata(new DocumentMetadata
        {
            Title = $"{model.Header.Title} · {model.Header.Heading}",
            Author = model.Header.Household,
            Creator = ReportPdfModelBuilder.Brand,
            Producer = ReportPdfModelBuilder.Brand,
            Language = model.Culture.Name,
        });
    }

    private static void Setup(PageDescriptor page, PageSize size, ReportPdfModel model, string lockup)
    {
        page.Size(size);
        page.Margin(36);
        page.PageColor(Colors.White);
        page.DefaultTextStyle(t => t.FontFamily(PdfCharts.FontFamily).FontSize(9.5f).FontColor(P.Ink));
        page.Header().BorderBottom(0.75f).BorderColor(P.Border).PaddingBottom(6).Row(row =>
        {
            row.ConstantItem(100).Height(24).Svg(lockup).FitArea();
            row.RelativeItem().AlignRight().AlignBottom().Text(t =>
            {
                t.Span(model.Header.Household).SemiBold();
                t.Span("  ·  " + model.Header.Heading).FontColor(P.Muted);
            });
        });
        page.Footer().PaddingTop(6).Row(row =>
        {
            row.RelativeItem().Text(model.Footer.Brand).FontSize(8).FontColor(P.Muted);
            row.RelativeItem().AlignRight().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(8).FontColor(P.Muted));
                t.Span(model.Footer.PageWord + " ");
                t.CurrentPageNumber();
                t.Span(" " + model.Footer.OfWord + " ");
                t.TotalPages();
            });
        });
    }

    private static void Report(ColumnDescriptor col, ReportPdfModel model)
    {
        var h = model.Header;
        col.Spacing(12);
        col.Item().Column(title =>
        {
            title.Item().Text(h.Title).FontSize(20).Bold().FontColor(P.Primary);
            title.Item().Text(h.Heading).FontSize(14).SemiBold();
            title.Item().Text($"{h.Period} · {h.Scope}").FontColor(P.Muted);
            title.Item().PaddingTop(2).Text(h.Rates is null ? h.Generated : $"{h.Generated} · {h.Rates}").FontSize(8).FontColor(P.Muted);
        });

        col.Item().Row(row =>
        {
            row.Spacing(8);
            foreach (var kpi in model.Kpis)
            {
                row.RelativeItem().Border(0.75f).BorderColor(P.Border).CornerRadius(6).Padding(8).Column(c =>
                {
                    c.Item().Text(kpi.Label).FontSize(8).FontColor(P.Muted);
                    // A "both" pair (₡ · $) prints one currency per line so neither figure wraps mid-number.
                    var sides = kpi.Value.Split(" · ");
                    c.Item().Text(sides[0]).FontSize(11).SemiBold();
                    foreach (var side in sides.Skip(1)) c.Item().Text(side).FontSize(9.5f).FontColor(P.Ink);
                    if (kpi.Sub is not null) c.Item().Text(kpi.Sub).FontSize(8).FontColor(P.Muted);
                });
            }
        });

        if (model.Pace is { } pace) col.Item().ShowEntire().Element(c => Card(c, pace, wide: true));
        if (model.EmptyNote is not null)
            col.Item().Background(P.Zebra).Border(0.75f).BorderColor(P.Border).Padding(10).Text(model.EmptyNote);

        // The donut cards first, two to a row (in the page's order), then the full-width bar cards — so no donut is left
        // alone on a row between two bar charts.
        var donuts = model.Charts.Where(c => !IsBars(c)).ToList();
        for (var i = 0; i < donuts.Count; i += 2)
        {
            var pair = donuts.Skip(i).Take(2).ToList();
            col.Item().ShowEntire().Row(row =>
            {
                row.Spacing(10);
                foreach (var chart in pair) row.RelativeItem().Element(c => Card(c, chart, wide: false));
                if (pair.Count == 1) row.RelativeItem();
            });
        }
        foreach (var chart in model.Charts.Where(IsBars))
            col.Item().ShowEntire().Element(c => Card(c, chart, wide: true));

        // INCOME-2: whose the month's income is — a few rows at most, so the heading, the rows and the total stay on one page.
        if (model.Income is { } income)
            col.Item().ShowEntire().Column(c =>
            {
                c.Item().PaddingTop(4).PaddingBottom(4).Text(income.Title).FontSize(14).SemiBold().FontColor(P.BrandDark);
                c.Item().Element(e => Table(e, income, fontSize: 9f, repeatHeader: true));
            });

        // A heading never ends a page: the section heading travels with the first table, and each table title with
        // its header and first rows — the block moves to the next page when there isn't room for that much.
        for (var i = 0; i < model.Categories.Count; i++)
        {
            var table = model.Categories[i];
            var first = i == 0;
            col.Item().EnsureSpace(first ? 130 : 90).Column(c =>
            {
                if (first) c.Item().PaddingTop(4).PaddingBottom(8).Text(model.CategoriesTitle).FontSize(14).SemiBold().FontColor(P.BrandDark);
                c.Item().Text(table.Title).FontSize(11).SemiBold();
                c.Item().PaddingTop(4).Element(e => Table(e, table, fontSize: 9f, repeatHeader: true));
            });
        }
    }

    private static bool IsBars(PdfChart chart) => chart.Key is "trend" or "method-budget";

    private static void Card(IContainer container, PdfChart chart, bool wide)
    {
        container.Border(0.75f).BorderColor(P.Border).CornerRadius(8).Padding(10).Column(c =>
        {
            c.Spacing(6);
            c.Item().Text(chart.Title).FontSize(11).SemiBold();
            if (chart.Caption is not null) c.Item().Text(chart.Caption).FontSize(8.5f).FontColor(P.Muted);

            if (chart.Svg is { } svg)
            {
                if (wide)
                {
                    c.Item().Element(e => Svg(e, svg));
                    if (chart.Legend.Count > 0) c.Item().Element(e => Legend(e, chart.Legend, inline: true));
                }
                else
                {
                    c.Item().Row(row =>
                    {
                        row.ConstantItem(96).Element(e => Svg(e, svg));
                        row.RelativeItem().PaddingLeft(8).AlignMiddle().Element(e => Legend(e, chart.Legend, inline: false));
                    });
                }
            }

            foreach (var note in chart.Notes)
                c.Item().Text(note.Text).FontSize(8.5f).FontColor(note.Alert ? P.Danger : P.Muted);
        });
    }

    /// <summary>Sizes the SVG from its own viewBox so bar charts of any height keep their proportions.</summary>
    private static void Svg(IContainer container, string svg)
    {
        var m = ViewBox().Match(svg);
        var ratio = m.Success
            ? double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
            : 1d;
        container.Layers(layers =>
        {
            layers.PrimaryLayer().AspectRatio((float)(1 / ratio)).Svg(svg).FitArea();
        });
    }

    [GeneratedRegex("viewBox=\"0 0 ([0-9.]+) ([0-9.]+)\"")]
    private static partial Regex ViewBox();

    private static void Legend(IContainer container, IReadOnlyList<PdfLegendItem> items, bool inline)
    {
        if (inline)
        {
            container.Row(row =>
            {
                row.Spacing(12);
                foreach (var item in items) row.AutoItem().Element(e => LegendEntry(e, item, stacked: false));
            });
            return;
        }
        container.Column(c =>
        {
            c.Spacing(3);
            foreach (var item in items) c.Item().Element(e => LegendEntry(e, item, stacked: true));
        });
    }

    /// <summary>A swatch and the label; the value goes under the label in a narrow card, beside it in a wide one.</summary>
    private static void LegendEntry(IContainer container, PdfLegendItem item, bool stacked) =>
        container.Row(row =>
        {
            row.ConstantItem(8).PaddingTop(2.5f).Height(8).Background(item.Color).CornerRadius(2);
            if (stacked)
            {
                row.RelativeItem().PaddingLeft(5).Column(c =>
                {
                    c.Item().Text(item.Label).FontSize(8.5f);
                    if (item.Value is not null) c.Item().Text(item.Value).FontSize(8f).FontColor(P.Muted);
                });
            }
            else
            {
                row.AutoItem().PaddingLeft(4).Text(item.Label).FontSize(8.5f);
                if (item.Value is not null) row.AutoItem().PaddingLeft(6).Text(item.Value).FontSize(8.5f).FontColor(P.Muted);
            }
        });

    private static void Table(IContainer container, PdfTable table, float fontSize, bool repeatHeader)
    {
        if (table.Rows.Count == 0)
        {
            container.Text(table.EmptyNote ?? "").FontSize(fontSize).FontColor(P.Muted);
            return;
        }
        container.DefaultTextStyle(s => s.FontSize(fontSize)).Table(t =>
        {
            t.ColumnsDefinition(cols =>
            {
                foreach (var column in table.Columns) cols.RelativeColumn(column.Width);
            });

            void Header(TableCellDescriptor header)
            {
                foreach (var column in table.Columns)
                {
                    var cell = header.Cell().Background(P.Zebra).BorderBottom(0.75f).BorderColor(P.Border).PaddingVertical(4).PaddingHorizontal(3);
                    (column.Right ? cell.AlignRight() : cell).Text(column.Header).SemiBold().FontColor(P.Muted);
                }
            }
            if (repeatHeader) t.Header(Header);

            foreach (var row in table.Rows) Row(t, table, row, bold: false);
            if (table.Total is { } total) Row(t, table, total, bold: true);
        });
    }

    private static void Row(TableDescriptor t, PdfTable table, IReadOnlyList<PdfCell> cells, bool bold)
    {
        for (var i = 0; i < cells.Count && i < table.Columns.Count; i++)
        {
            var cell = t.Cell().BorderBottom(bold ? 0 : 0.5f).BorderTop(bold ? 0.75f : 0).BorderColor(P.Border).PaddingVertical(3).PaddingHorizontal(3);
            var text = (table.Columns[i].Right ? cell.AlignRight() : cell).Text(cells[i].Text).FontColor(cells[i].Color ?? P.Ink);
            if (bold) text.SemiBold();
        }
    }
}
