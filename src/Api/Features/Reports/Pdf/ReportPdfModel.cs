using System.Globalization;
using Vuelto.Core.Budget;

namespace Vuelto.Api.Features.Reports.Pdf;

// REPORTS-7 (ADR-V022): what the PDF is built from, and the laid-out-ready model the builder turns it into.
// Every string in the model is already localized and formatted and every chart is already SVG, so the QuestPDF
// document only arranges it — the numbers are tested here, the layout is walked in QA.

/// <summary>The month a single-month report is about (for the heading), by its budget year and number.</summary>
public sealed record ReportPdfMonth(int Year, int Number);

/// <summary>How the caller asked to see it: the "show in" side, the chart currency, the appendix, the language, and the device date for the pace marker.</summary>
public sealed record ReportPdfOptions(string Display, string ChartCurrency, bool IncludeAppendix, string Language, DateOnly Today)
{
    public CultureInfo Culture => CultureInfo.GetCultureInfo(Language);
}

/// <summary>
/// Everything the page shows for the period: the category analysis (the same response the page reads), the months
/// trend and the month's pending refunds (single month only), and the CSV export's rows when the appendix is wanted.
/// </summary>
public sealed record ReportPdfInput(
    string HouseholdName,
    DateTimeOffset GeneratedAt,
    CategoryAnalysisResponse Analysis,
    ReportPdfMonth? Month,
    MonthsTrendResponse? Trend,
    MoneyPair? PendingRefunds,
    IReadOnlyList<TransactionExportRow>? Appendix,
    ReportPdfOptions Options);

public sealed record ReportPdfModel(
    CultureInfo Culture,
    ReportPdfHeader Header,
    IReadOnlyList<PdfKpi> Kpis,
    PdfChart? Pace,
    string? EmptyNote,
    IReadOnlyList<PdfChart> Charts,
    string CategoriesTitle,
    IReadOnlyList<PdfTable> Categories,
    PdfTable? Appendix,
    ReportPdfFooter Footer,
    PdfTable? Income = null);

/// <summary><see cref="Rates"/> is the buy/sell pair line, the "no rate" sentence for a month without one, or null for a range.</summary>
public sealed record ReportPdfHeader(string Title, string Household, string Heading, string Period, string Scope, string Generated, string? Rates);

public sealed record PdfKpi(string Label, string Value, string? Sub);

/// <summary>A chart card. <see cref="Svg"/> is null when there is nothing to draw (the notes say why).</summary>
public sealed record PdfChart(string Key, string Title, string? Caption, string? Svg, IReadOnlyList<PdfLegendItem> Legend, IReadOnlyList<PdfNote> Notes);

public sealed record PdfLegendItem(string Label, string Color, string? Value);

/// <summary>A sentence under a chart; <see cref="Alert"/> ones (an overrun) print in the danger colour.</summary>
public sealed record PdfNote(string Text, bool Alert = false);

public sealed record PdfTable(string Title, IReadOnlyList<PdfColumn> Columns, IReadOnlyList<IReadOnlyList<PdfCell>> Rows, IReadOnlyList<PdfCell>? Total, string? EmptyNote);

public sealed record PdfColumn(string Header, float Width, bool Right = false);

/// <summary>One cell; <see cref="Color"/> is a literal hex (red over / green under budget) or null for the ink colour.</summary>
public sealed record PdfCell(string Text, string? Color = null);

/// <summary>The footer reads "{Brand} · {PageWord} n {OfWord} m".</summary>
public sealed record ReportPdfFooter(string Brand, string PageWord, string OfWord);
