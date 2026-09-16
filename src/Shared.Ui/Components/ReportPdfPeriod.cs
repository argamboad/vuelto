namespace Vuelto.Shared.Ui.Components;

/// <summary>The period a report PDF covers (REPORTS-7): a budget month by id, or an inclusive date range.</summary>
public sealed record ReportPdfPeriod(Guid? MonthId, DateOnly? From, DateOnly? To);
