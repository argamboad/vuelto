using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Reports;

/// <summary>
/// REPORTS-1/2: resolves the period (a month's anchor window — first week start to the <b>last week's
/// end date</b>, never <c>WeekCount × 7</c> (WU-4 A3) — or an explicit inclusive range), then either
/// groups spend by class and category (pure <see cref="CategoryAnalysisCalculator"/>) or renders every
/// matching transaction as CSV, stores it through <see cref="IFileStorage"/> and returns a signed link.
/// Reads only, tenant-filtered through <c>Query()</c>; names come from the all-states catalogs.
/// </summary>
public sealed class ReportHandler(
    IRepository<Month> months,
    IRepository<Week> weeks,
    IRepository<Transaction> transactions,
    IRepository<Category> categories,
    IRepository<Bank> banks,
    IRepository<FixedExpense> fixedExpenses,
    IRepository<VariableExpense> variableExpenses,
    IFileStorage files,
    TimeProvider clock)
{
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Either a period, or the 400 to return; <c>NotFound</c> when <c>month_id</c> names no month of this household (uniform 404).</summary>
    public sealed record PeriodResolution(ReportPeriod? Period, ErrorResponse? Error, bool NotFound = false);

    public async Task<PeriodResolution> ResolvePeriodAsync(Guid? monthId, string? from, string? to, CancellationToken cancellationToken)
    {
        var hasRange = from is not null || to is not null;
        if (monthId is null && !hasRange)
            return new(null, new ErrorResponse("period_required", "Provide month_id, or from and to."));
        if (monthId is not null && hasRange)
            return new(null, new ErrorResponse("period_ambiguous", "Provide month_id or from and to, not both."));

        if (monthId is { } id)
        {
            var month = await months.Query().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
            if (month is null) return new(null, null, NotFound: true);
            var monthWeeks = await weeks.Query().Where(w => w.MonthId == id).ToListAsync(cancellationToken);
            var end = monthWeeks.Count > 0 ? monthWeeks.Max(w => w.EndDate) : month.Week1StartDate.AddDays(month.WeekCount * 7 - 1);
            return new(new ReportPeriod(month.Week1StartDate, end, id), null);
        }

        if (from is null || to is null)
            return new(null, new ErrorResponse("period_incomplete", "Both from and to are required."));
        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var finish))
            return new(null, new ErrorResponse("period_invalid", "Dates must be yyyy-MM-dd."));
        if (start > finish)
            return new(null, new ErrorResponse("period_invalid", "from must not be after to."));
        return new(new ReportPeriod(start, finish, null), null);
    }

    public async Task<CategoryAnalysisResponse> AnalyzeAsync(ReportPeriod period, CancellationToken cancellationToken)
    {
        var rows = await InPeriod(period).ToListAsync(cancellationToken);
        var names = await categories.Query().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken); // all states

        List<IExpenseLine>? lines = null;
        if (period.SingleMonth)
        {
            lines = [];
            lines.AddRange(await fixedExpenses.Query().Where(l => l.IsActive).ToListAsync(cancellationToken));
            lines.AddRange(await variableExpenses.Query().Where(l => l.IsActive).ToListAsync(cancellationToken));
        }

        return CategoryAnalysisResponse.From(CategoryAnalysisCalculator.Calculate(rows, names, period.From, period.To, lines));
    }

    /// <summary>Every matching transaction (unpaginated), date desc then created desc, as a stored CSV behind a 15-minute signed link.</summary>
    public async Task<TransactionExportResponse> ExportAsync(ReportPeriod period, Guid? categoryId, string? transactionType, CancellationToken cancellationToken)
    {
        var query = InPeriod(period);
        if (categoryId is { } cat) query = query.Where(t => t.CategoryId == cat);
        if (!string.IsNullOrWhiteSpace(transactionType))
        {
            var type = TransactionTypes.Normalize(transactionType) ?? transactionType.Trim().ToLowerInvariant();
            query = query.Where(t => t.TransactionType == type);
        }
        var rows = await query.OrderByDescending(t => t.TransactionDate).ThenByDescending(t => t.CreatedAt).ToListAsync(cancellationToken);

        var categoryNames = await categories.Query().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var bankNames = await banks.Query().ToDictionaryAsync(b => b.Id, b => b.Name, cancellationToken);

        var csv = TransactionCsvWriter.Write(rows.Select(t => new TransactionExportRow(
            t.TransactionDate, t.Payee, categoryNames.GetValueOrDefault(t.CategoryId), t.TransactionType,
            t.AmountCrc, t.AmountUsd, t.ExchangeRateUsed, t.PaymentMethod, bankNames.GetValueOrDefault(t.BankId), t.Source)));

        // The download filename is the key's basename (server-controlled); a per-export folder keeps two
        // members exporting at the same second from overwriting each other's file.
        var now = clock.GetUtcNow();
        var fileName = $"transactions-{now:yyyy-MM-dd}.csv";
        var key = $"exports/transactions/{now:yyyyMMddTHHmmssZ}-{Guid.CreateVersion7():N}/{fileName}";
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv)))
            await files.PutAsync(key, stream, "text/csv; charset=utf-8", cancellationToken);
        var url = await files.GetDownloadUrlAsync(key, LinkLifetime, cancellationToken);

        return new TransactionExportResponse(url.ToString(), fileName, rows.Count, new ReportPeriodResponse(period.From, period.To), (int)LinkLifetime.TotalSeconds);
    }

    private IQueryable<Transaction> InPeriod(ReportPeriod p) =>
        transactions.Query().Where(t => t.TransactionDate >= p.From && t.TransactionDate <= p.To);
}
