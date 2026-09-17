using System.Globalization;
using System.Resources;
using Vuelto.Core.Budget;
using P = Vuelto.Api.Features.Reports.Pdf.PdfCharts.Palette;

namespace Vuelto.Api.Features.Reports.Pdf;

/// <summary>
/// REPORTS-7: turns what the Reports page shows into the PDF's model. Pure — no I/O, no clock, no QuestPDF — and a
/// rule-for-rule mirror of <c>Shared.Ui/Pages/Reports.razor</c> and <c>CategoryTable.razor</c>: the four tiles and
/// their subtitles, the pace (a month picture, in the "show in" currency or the chart currency for "both"), the
/// income/budget cards that say why when there is no rate, the category tables (budget and red/green only for the
/// budgeted class of a month, judged in each line's own currency), the month's income by whose it is (INCOME-2: a donut
/// and a table), and the appendix of the CSV's rows.
/// </summary>
public static class ReportPdfModelBuilder
{
    public const string Brand = "¿Y el vuelto?";

    private static readonly ResourceManager Strings = new("Vuelto.Api.Features.Reports.Pdf.ReportPdfStrings", typeof(ReportPdfModelBuilder).Assembly);

    public static ReportPdfModel Build(ReportPdfInput input) => new Builder(input).Build();

    /// <summary>
    /// REPORTS-8: the subject and the one-sentence body of the email that carries the PDF — the heading, the period,
    /// the household and the total spend exactly as the first tile prints them, in the model's language.
    /// </summary>
    public static (string Subject, string Body) EmailText(ReportPdfModel model)
    {
        string T(string key) => Strings.GetString("Pdf_" + key, model.Culture) ?? key;
        var h = model.Header;
        return (string.Format(model.Culture, T("EmailSubject"), h.Heading),
            string.Format(model.Culture, T("EmailBody"), h.Heading, h.Period, h.Household, model.Kpis[0].Value));
    }

    private sealed class Builder(ReportPdfInput input)
    {
        private readonly CategoryAnalysisResponse _a = input.Analysis;
        private readonly ReportPdfOptions _o = input.Options;
        private readonly CultureInfo _c = input.Options.Culture;

        private string T(string key) => Strings.GetString("Pdf_" + key, _c) ?? key;
        private string T(string key, params object[] args) => string.Format(_c, T(key), args);

        public ReportPdfModel Build()
        {
            var empty = _a.Budgeted.Count == 0 && _a.Extraordinary.Count == 0 && _a.UnplannedEssential.Count == 0;
            return new ReportPdfModel(
                _c,
                Header(),
                Kpis(),
                _a.SingleMonth ? Pace() : null,
                empty ? T("Empty") : null,
                empty ? [] : Charts(),
                T("ByCategory"),
                empty ? [] : Categories(),
                _o.IncludeAppendix ? Appendix() : null,
                new ReportPdfFooter(Brand, T("FooterPage"), T("FooterOf")),
                IncomeTable());
        }

        // ---- formatting ----

        private string Num(decimal v, string format = "N2") => v.ToString(format, _c);

        /// <summary>A converted pair on the "show in" side(s).</summary>
        private string Show(decimal crc, decimal usd) => _o.Display switch
        {
            DisplayCurrencies.Crc => "₡" + Num(crc),
            DisplayCurrencies.Usd => "$" + Num(usd),
            _ => $"₡{Num(crc)} · ${Num(usd)}",
        };

        /// <summary>A single-currency budget on its own side (a sum of both kinds shows both).</summary>
        private string Budget(decimal crc, decimal usd) =>
            usd == 0 && crc != 0 ? "₡" + Num(crc)
            : crc == 0 && usd != 0 ? "$" + Num(usd)
            : $"₡{Num(crc)} · ${Num(usd)}";

        /// <summary>Percentages need one side; "both" measures in colones.</summary>
        private decimal Side(decimal crc, decimal usd) => _o.Display == DisplayCurrencies.Usd ? usd : crc;

        private static int Pct(decimal part, decimal whole) => whole <= 0 ? 0 : (int)Math.Round(100m * part / whole);

        private bool ChartUsd => _o.ChartCurrency == Currencies.Usd;
        private decimal ChartSide(decimal crc, decimal usd) => ChartUsd ? usd : crc;
        private string Amount(decimal v) => (ChartUsd ? "$" : "₡") + Num(v);
        private string Amount0(decimal v) => (ChartUsd ? "$" : "₡") + Num(v, "N0");

        private string Date(DateOnly d) => d.ToString("d MMM yyyy", _c);

        private string MonthLabel(int year, int month) =>
            $"{_c.TextInfo.ToTitleCase(_c.DateTimeFormat.GetMonthName(month))} {year}";

        private string ClassLabel(string code) => code switch
        {
            TransactionTypes.Budgeted => T("ClassBudgeted"),
            TransactionTypes.Extraordinary => T("ClassExtraordinary"),
            TransactionTypes.UnplannedEssential => T("ClassUnplanned"),
            TransactionTypes.Inflow => T("ClassInflow"),
            TransactionTypes.EnvelopeContribution => T("ClassEnvelope"),
            _ => code,
        };

        private string MethodLabel(string code) => code switch
        {
            PaymentMethods.CreditCard => T("MethodCreditCard"),
            PaymentMethods.BankAccount => T("MethodBankAccount"),
            _ => code,
        };

        private string SourceLabel(string code) => code switch
        {
            TransactionSources.Manual => T("SourceManual"),
            TransactionSources.Email => T("SourceEmail"),
            TransactionSources.RefundRealization => T("SourceRefund"),
            _ => code,
        };

        // ---- header ----

        private ReportPdfHeader Header()
        {
            var period = T("Period", Date(_a.Period.From), Date(_a.Period.To));
            var heading = input.Month is { } m ? MonthLabel(m.Year, m.Number) : period;
            string? rates = !_a.SingleMonth ? null
                : _a.ExchangeRate is { } sell && _a.ExchangeRateBuy is { } buy ? T("Rates", "₡" + Num(buy), "₡" + Num(sell))
                : T("NoRate");
            return new ReportPdfHeader(
                T("Title"), input.HouseholdName, heading, period,
                T(_a.SingleMonth ? "ScopeMonth" : "ScopeRange"),
                T("Generated", input.GeneratedAt.UtcDateTime.ToString("d MMM yyyy HH:mm", _c)),
                rates);
        }

        // ---- tiles ----

        private (decimal Crc, decimal Usd) Sum(params IReadOnlyList<CategorySpendResponse>[] lists) =>
            (lists.Sum(l => l.Sum(e => e.TotalCrc)), lists.Sum(l => l.Sum(e => e.TotalUsd)));

        private List<PdfKpi> Kpis()
        {
            var spend = Sum(_a.Budgeted, _a.Extraordinary, _a.UnplannedEssential);
            var budgeted = Sum(_a.Budgeted);
            var discretionary = Sum(_a.Extraordinary);
            var unplanned = Sum(_a.UnplannedEssential);
            string OfSpend((decimal Crc, decimal Usd) part) => T("OfSpend", Pct(Side(part.Crc, part.Usd), Side(spend.Crc, spend.Usd)));

            var refundable = input.PendingRefunds is { } r && Side(r.Crc, r.Usd) > 0
                ? T("Refundable", _o.Display == DisplayCurrencies.Usd ? "$" + Num(r.Usd) : "₡" + Num(r.Crc))
                : OfSpend(unplanned);
            return
            [
                new(T("KpiTotalSpend"), Show(spend.Crc, spend.Usd),
                    _a.Income is { } inc ? T("OfIncome", Pct(Side(spend.Crc, spend.Usd), Side(inc.Crc, inc.Usd))) : null),
                new(T("KpiBudgeted"), Show(budgeted.Crc, budgeted.Usd), OfSpend(budgeted)),
                new(T("KpiDiscretionary"), Show(discretionary.Crc, discretionary.Usd), OfSpend(discretionary)),
                new(T("KpiUnplanned"), Show(unplanned.Crc, unplanned.Usd), refundable),
            ];
        }

        // ---- pace ----

        private bool PaceUsd => (_o.Display == DisplayCurrencies.Both ? _o.ChartCurrency : _o.Display) == Currencies.Usd;
        private decimal PaceSide(decimal crc, decimal usd) => PaceUsd ? usd : crc;

        private PdfChart Pace()
        {
            var plan = _a.BudgetTotal is { } b ? PaceSide(b.Crc, b.Usd) : 0m;
            var points = new List<PdfPoint>();
            decimal running = 0;
            foreach (var d in _a.SpendByDay ?? [])
            {
                running += PaceSide(d.TotalCrc, d.TotalUsd);
                points.Add(new(d.Date, running));
            }
            var spend = Sum(_a.Budgeted, _a.Extraordinary, _a.UnplannedEssential);
            var spent = PaceSide(spend.Crc, spend.Usd);

            string? caption = null;
            if (_a.BudgetTotal is not null)
            {
                var total = _a.Period.To.DayNumber - _a.Period.From.DayNumber + 1;
                var elapsed = Math.Clamp(_o.Today.DayNumber - _a.Period.From.DayNumber + 1, 0, total);
                caption = T("PaceCaption", Math.Round(100m * elapsed / total), plan > 0 ? Math.Round(spent / plan * 100m) : 0m);
            }

            var svg = PdfCharts.Line(points, plan, _a.Period.From, _a.Period.To, _o.Today,
                v => (PaceUsd ? "$" : "₡") + Num(v, "N0"), d => d.ToString("d MMM", _c), T("ChartToday"));
            var legend = new List<PdfLegendItem> { new(T("ChartActual"), P.Primary, null) };
            if (plan > 0) legend.Add(new(T("ChartPlan"), P.AccentLight, null));
            legend.Add(new(T("ChartToday"), P.Warning, null));
            var notes = new List<PdfNote>();
            if (_a.BudgetTotal is null) notes.Add(new(T("PaceNoRate")));
            if (svg is null) notes.Add(new(T("ChartEmpty")));
            return new PdfChart("pace", T("Pace"), caption, svg, svg is null ? [] : legend, notes);
        }

        // ---- charts ----

        private List<PdfSlice> ClassSlices() =>
        [
            new(T("ClassBudgeted"), _a.Budgeted.Sum(e => ChartSide(e.TotalCrc, e.TotalUsd)), P.Primary),
            new(T("ClassExtraordinary"), _a.Extraordinary.Sum(e => ChartSide(e.TotalCrc, e.TotalUsd)), P.AccentLight),
            new(T("ClassUnplanned"), _a.UnplannedEssential.Sum(e => ChartSide(e.TotalCrc, e.TotalUsd)), P.Warning),
        ];

        /// <summary>A donut card: legend "amount · share" per slice, the hole saying <paramref name="center"/> (default: the total).</summary>
        private PdfChart Donut(string key, string title, List<PdfSlice> slices, string? center = null, List<PdfNote>? notes = null)
        {
            var total = slices.Sum(s => Math.Max(s.Value, 0));
            var svg = PdfCharts.Donut(slices, center ?? Amount0(total));
            var legend = slices.Select(s => new PdfLegendItem(s.Label, s.Color,
                $"{Amount0(s.Value)} · {(total > 0 ? s.Value / total : 0).ToString("P0", _c)}")).ToList();
            notes ??= [];
            if (svg is null) notes.Add(new(T("ChartEmpty")));
            return new PdfChart(key, title, null, svg, svg is null ? [] : legend, notes);
        }

        private PdfChart NoRateCard(string key, string title) => new(key, title, null, null, [], [new(T("IncomeNoRate"))]);

        private List<PdfChart> Charts()
        {
            var charts = new List<PdfChart> { Donut("class", T("ClassSplit"), ClassSlices()) };
            var spend = ClassSlices().Sum(s => s.Value);

            if (_a.SingleMonth)
            {
                if (_a.Income is { } inc)
                {
                    var income = ChartSide(inc.Crc, inc.Usd);
                    var slices = ClassSlices();
                    slices.Add(new(T("Remaining"), Math.Max(0m, income - spend), P.Rail));
                    var notes = spend > income ? new List<PdfNote> { new(T("OverBy", Amount(spend - income)), Alert: true) } : [];
                    charts.Add(Donut("income", T("IncomeSplit"), slices, Amount0(income), notes));
                }
                else charts.Add(NoRateCard("income", T("IncomeSplit")));

                if (_a.Income is { } inc2 && _a.BudgetTotal is { } bt)
                {
                    var income = ChartSide(inc2.Crc, inc2.Usd);
                    var planned = ChartSide(bt.Crc, bt.Usd);
                    var notes = planned > income ? new List<PdfNote> { new(T("BudgetOverBy", Amount(planned - income)), Alert: true) } : [];
                    charts.Add(Donut("budget", T("BudgetSplit"),
                        [new(T("BudgetLines"), planned, P.Primary), new(T("Uncommitted"), Math.Max(0m, income - planned), P.Rail)],
                        Amount0(income), notes));
                }
                else charts.Add(NoRateCard("budget", T("BudgetSplit")));

                if (_a.IncomeByMember is { Count: > 0 } byMember)
                    charts.Add(Donut("members", T("IncomeByMember"), byMember
                        .Select((s, i) => new PdfSlice(WhoLabel(s), ChartSide(s.Amount.Crc, s.Amount.Usd), P.Series[i % P.Series.Count]))
                        .ToList()));

                if (input.Trend is { Months.Count: > 0 } trend)
                {
                    var bars = trend.Months.Select(m => new PdfBar(MonthLabel(m.Year, m.MonthNumber), ChartSide(m.Spend.Crc, m.Spend.Usd),
                        m.Income is null ? null : ChartSide(m.Income.Crc, m.Income.Usd))).ToList();
                    charts.Add(new PdfChart("trend", T("Trend"), null, PdfCharts.Bars(bars, Amount),
                        [new(T("TrendSpend"), P.Primary, null), new(T("TrendIncome"), P.Tertiary, null), new(T("TrendOver"), P.Danger, null)],
                        trend.RateAvailable ? [] : [new(T("TrendNoRate"))]));
                }
            }

            charts.Add(Donut("bank", T("ByBank"), _a.ByBank
                .Select((b, i) => new PdfSlice(string.IsNullOrEmpty(b.Label) ? T("UnknownBank") : b.Label, ChartSide(b.TotalCrc, b.TotalUsd), P.Series[i % P.Series.Count]))
                .ToList()));

            if (_a.ByCard.Any(c => c.Key != "none"))
                charts.Add(Donut("card", T("ByCard"), _a.ByCard
                    .Select((c, i) => new PdfSlice(c.Key == "none" ? T("NoCard") : c.Label, ChartSide(c.TotalCrc, c.TotalUsd), P.Series[i % P.Series.Count]))
                    .ToList()));

            charts.Add(Donut("method", T("ByMethod"), _a.ByMethod
                .Select(m => new PdfSlice(MethodLabel(m.Key), ChartSide(m.TotalCrc, m.TotalUsd), m.Key == PaymentMethods.BankAccount ? P.AccentLight : P.Primary))
                .ToList()));

            if (_a.BudgetByMethod is { Count: > 0 } byMethod)
            {
                var spent = _a.ByMethod.ToDictionary(m => m.Key, m => ChartSide(m.TotalCrc, m.TotalUsd));
                var bars = byMethod.Select(b => new PdfBar(MethodLabel(b.Key), spent.GetValueOrDefault(b.Key), ChartSide(b.TotalCrc, b.TotalUsd))).ToList();
                charts.Add(new PdfChart("method-budget", T("MethodBudget"), null, PdfCharts.Bars(bars, Amount),
                    [new(T("TrendSpend"), P.Primary, null), new(T("ChartBudget"), P.Tertiary, null), new(T("ChartOverBudget"), P.Danger, null)],
                    bars.Select(b => new PdfNote($"{b.Label}: {T("MethodCaption", Amount(b.Track ?? 0), Amount(b.Value))}")).ToList()));
            }
            return charts;
        }

        // ---- income by member (INCOME-2) ----

        private string WhoLabel(IncomeMemberResponse s) => s.Kind switch
        {
            IncomeByMember.Member => s.Name ?? T("WhoFormer"),
            IncomeByMember.Household => T("WhoHousehold"),
            IncomeByMember.FormerMember => T("WhoFormer"),
            IncomeByMember.Inflows => T("WhoInflows"),
            _ => s.Kind,
        };

        /// <summary>The month's income by whose it is, on the "show in" side, with each slice's share; null without it (a range, or no rate).</summary>
        private PdfTable? IncomeTable()
        {
            if (!_a.SingleMonth || _a.Income is not { } income || _a.IncomeByMember is not { } slices) return null;
            var columns = new List<PdfColumn> { new(T("ColWho"), 5f), new(T("ColIncome"), 4f, Right: true), new(T("ColShare"), 1.5f, Right: true) };
            var whole = Side(income.Crc, income.Usd);
            var rows = slices.Select(s => (IReadOnlyList<PdfCell>)
            [
                new(WhoLabel(s)),
                new(Show(s.Amount.Crc, s.Amount.Usd)),
                new(Pct(Side(s.Amount.Crc, s.Amount.Usd), whole).ToString(_c) + "%"),
            ]).ToList();
            IReadOnlyList<PdfCell> total = [new(T("Total")), new(Show(income.Crc, income.Usd)), new(rows.Count > 0 ? "100%" : "")];
            return new PdfTable(T("IncomeTitle"), columns, rows, rows.Count > 0 ? total : null, rows.Count == 0 ? T("IncomeEmpty") : null);
        }

        // ---- category tables ----

        private List<PdfTable> Categories() =>
        [
            ClassTable(T("ClassBudgeted"), _a.Budgeted, _a.SingleMonth),
            ClassTable(T("ClassExtraordinary"), _a.Extraordinary, false),
            ClassTable(T("ClassUnplanned"), _a.UnplannedEssential, false),
        ];

        /// <summary>Spend over budget in the line's OWN currency (null: no budget) — the page's <c>ReportCategoryEntry.Fill</c>.</summary>
        private static decimal? Fill(CategorySpendResponse e) => e switch
        {
            { BudgetedCrc: > 0 } => e.TotalCrc / e.BudgetedCrc.Value,
            { BudgetedUsd: > 0 } => e.TotalUsd / e.BudgetedUsd.Value,
            _ => null,
        };

        private PdfTable ClassTable(string title, IReadOnlyList<CategorySpendResponse> entries, bool showBudget)
        {
            var columns = new List<PdfColumn> { new(T("ColCategory"), 5f), new(T("ColCount"), 1f, Right: true) };
            if (showBudget) columns.Add(new(T("ColBudgeted"), 3f, Right: true));
            columns.Add(new(T(showBudget ? "ColActual" : "ColSpent"), 4f, Right: true));

            if (entries.Count == 0) return new PdfTable(title, columns, [], null, T("NoneInClass"));

            var rows = entries.Select(e =>
            {
                var cells = new List<PdfCell> { new(e.CategoryName), new(e.TransactionCount.ToString(_c)) };
                if (showBudget) cells.Add(new(e.BudgetedCrc is null ? "—" : Budget(e.BudgetedCrc.Value, e.BudgetedUsd ?? 0)));
                var tone = showBudget ? Fill(e) switch { null => null, > 1 => P.Danger, _ => P.Success } : null;
                cells.Add(new(Show(e.TotalCrc, e.TotalUsd), tone));
                return (IReadOnlyList<PdfCell>)cells;
            }).ToList();

            var total = new List<PdfCell> { new(T("Total")), new(entries.Sum(e => e.TransactionCount).ToString(_c)) };
            if (showBudget) total.Add(new(BudgetTotal(entries)));
            total.Add(new(Show(entries.Sum(e => e.TotalCrc), entries.Sum(e => e.TotalUsd))));
            return new PdfTable(title, columns, rows, total, null);
        }

        /// <summary>The rows' budgets as ONE figure — dollar lines at the sell rate, colón lines at buy; without a rate, each side's own sum.</summary>
        private string BudgetTotal(IReadOnlyList<CategorySpendResponse> entries)
        {
            var crc = entries.Sum(x => x.BudgetedCrc ?? 0);
            var usd = entries.Sum(x => x.BudgetedUsd ?? 0);
            return _a.ExchangeRate is { } sell && _a.ExchangeRateBuy is { } buy && buy > 0
                ? Show(crc + usd * sell, crc / buy + usd)
                : Budget(crc, usd);
        }

        // ---- appendix ----

        /// <summary>The CSV's rows, one column per chosen field (REPORTS-9: date and payee always; the rest as asked).</summary>
        private PdfTable Appendix()
        {
            var crc = _o.Display != DisplayCurrencies.Usd;
            var usd = _o.Display != DisplayCurrencies.Crc;
            var fields = new List<(PdfColumn Column, Func<TransactionExportRow, string> Cell)>
            {
                (new(T("ColDate"), 2.2f), r => r.Date.ToString("d", _c)),
                (new(T("ColPayee"), 4.5f), r => r.Payee),
            };
            if (_o.Shows(ReportPdfColumns.Category)) fields.Add((new(T("ColCategory"), 3.2f), r => r.CategoryName ?? ""));
            if (_o.Shows(ReportPdfColumns.Class)) fields.Add((new(T("ColClass"), 2.8f), r => ClassLabel(r.TransactionType)));
            if (_o.Shows(ReportPdfColumns.Amount))
            {
                if (crc) fields.Add((new("₡", 3f, Right: true), r => "₡" + Num(r.AmountCrc)));
                if (usd) fields.Add((new("$", 2.2f, Right: true), r => "$" + Num(r.AmountUsd)));
            }
            if (_o.Shows(ReportPdfColumns.Rate)) fields.Add((new(T("ColRate"), 1.8f, Right: true), r => Num(r.ExchangeRateUsed)));
            if (_o.Shows(ReportPdfColumns.Method)) fields.Add((new(T("ColMethod"), 2.8f), r => MethodLabel(r.PaymentMethod)));
            if (_o.Shows(ReportPdfColumns.Bank)) fields.Add((new(T("ColBank"), 2.4f), r => r.BankName ?? ""));
            if (_o.Shows(ReportPdfColumns.Source)) fields.Add((new(T("ColSource"), 1.8f), r => SourceLabel(r.Source)));
            if (_o.Shows(ReportPdfColumns.Card)) fields.Add((new(T("ColCard"), 2.6f), r => r.CardName ?? ""));
            if (_o.Shows(ReportPdfColumns.Notes)) fields.Add((new(T("ColNotes"), 4f), r => r.Notes ?? ""));

            var rows = (input.Appendix ?? [])
                .Select(r => (IReadOnlyList<PdfCell>)fields.Select(f => new PdfCell(f.Cell(r))).ToList())
                .ToList();
            return new PdfTable(T("AppendixTitle"), fields.Select(f => f.Column).ToList(), rows, null, rows.Count == 0 ? T("AppendixEmpty") : null);
        }
    }
}
