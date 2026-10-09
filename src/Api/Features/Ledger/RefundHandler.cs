using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Ledger;

/// <summary>
/// LEDGER-3: a month's expected refunds and the one thing edited directly on them — the status.
/// <c>pending → received</c> books a derived <c>inflow</c> (same amounts and frozen rate, the source
/// transaction's bank and category, <c>source = refund_realization</c>) <b>dated the day the money
/// landed and filed in that day's month</b> (ADR-V017 — the refund itself stays in its purchase's
/// month), auto-creating the month like any transaction; <c>received → pending</c> removes it (and
/// its month if emptied). The flip is a <b>conditional update</b> inside a
/// unit-of-work scope (ADR-V014): two concurrent flips book exactly one inflow — the loser rolls back
/// and gets <c>refund_status_conflict</c>. Same status is a no-op.
/// </summary>
public sealed class RefundHandler(
    IRepository<Refund> refunds,
    IRepository<Transaction> transactions,
    MonthHandler months,
    IUnitOfWork unitOfWork,
    ICurrentTenant tenant,
    TimeProvider clock,
    ILogger<RefundHandler> logger)
{
    /// <summary>The month's refunds newest first, with their received / pending totals (#206). Null = month not found (uniform 404).</summary>
    public async Task<RefundListResponse?> ListForMonthAsync(Guid monthId, CancellationToken cancellationToken)
    {
        if (await months.GetAsync(monthId, cancellationToken) is null) return null;
        var rows = await refunds.Query().Where(r => r.MonthId == monthId)
            .OrderByDescending(r => r.TransactionDate).ThenByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);
        var inflowMonths = await InflowMonthsAsync(rows, cancellationToken);
        var sourceIds = rows.Select(r => r.TransactionId).ToList();
        var sources = await transactions.Query().Where(t => sourceIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);
        return new RefundListResponse(
            rows.Select(r => RefundResponse.From(r, r.InflowTransactionId is { } i ? inflowMonths.GetValueOrDefault(i) : null, sources.GetValueOrDefault(r.TransactionId))).ToList(),
            RefundTotalsResponse.From(RefundTotals.Calculate(rows)));
    }

    /// <summary>The refund's purchase — for the percentage the response computes (ADR-V026: never stored).</summary>
    private Task<Transaction?> SourceAsync(Refund refund, CancellationToken cancellationToken) =>
        transactions.Query().FirstOrDefaultAsync(t => t.Id == refund.TransactionId, cancellationToken);

    /// <summary>The most rows the cross-month list returns; a household past it narrows the filter (the totals still cover all).</summary>
    public const int ListCap = 1000;

    /// <summary>
    /// #208: every refund of the household across months, oldest purchase first (the ones to chase lead), filtered by
    /// status, payee fragment and purchase-date range, with the purchase's budget month, the days a pending refund has
    /// been out, and the totals of everything that matched. Tenant-scoped through <c>Query()</c>.
    /// </summary>
    public async Task<(RefundListResponse? List, ErrorResponse? Error)> ListAsync(RefundQuery query, CancellationToken cancellationToken)
    {
        string? status = null;
        if (!string.IsNullOrWhiteSpace(query.Status) && (status = RefundStatuses.Normalize(query.Status)) is null)
            return (null, new ErrorResponse("invalid_request", "status must be pending or received"));
        if (query.From is { } from && query.To is { } to && from > to)
            return (null, new ErrorResponse("invalid_request", "from must be on or before to"));

        var q = refunds.Query();
        if (status is not null) q = q.Where(r => r.Status == status);
        if (!string.IsNullOrWhiteSpace(query.Payee))
        {
            var fragment = "%" + query.Payee.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            q = q.Where(r => EF.Functions.ILike(r.Payee, fragment));
        }
        if (query.From is { } f) q = q.Where(r => r.TransactionDate >= f);
        if (query.To is { } t) q = q.Where(r => r.TransactionDate <= t);

        var all = await q.OrderBy(r => r.TransactionDate).ThenBy(r => r.CreatedAt).ToListAsync(cancellationToken);
        var rows = all.Take(ListCap).ToList();

        var inflowMonths = await InflowMonthsAsync(rows, cancellationToken);
        var sourceIds = rows.Select(r => r.TransactionId).ToList();
        var sources = await transactions.Query().Where(x => sourceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var monthIds = rows.Select(r => r.MonthId).Distinct().ToList();
        var monthsById = await months.QueryMonths().Where(m => monthIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Year, m.MonthNumber }).ToDictionaryAsync(m => m.Id, cancellationToken);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var items = rows.Select(r =>
        {
            var month = monthsById.GetValueOrDefault(r.MonthId);
            return RefundResponse.From(r, r.InflowTransactionId is { } i ? inflowMonths.GetValueOrDefault(i) : null, sources.GetValueOrDefault(r.TransactionId)) with
            {
                MonthYear = month?.Year,
                MonthNumber = month?.MonthNumber,
                PendingDays = r.Status == RefundStatuses.Pending ? Math.Max(0, today.DayNumber - r.TransactionDate.DayNumber) : null,
            };
        }).ToList();
        return (new RefundListResponse(items, RefundTotalsResponse.From(RefundTotals.Calculate(all)), all.Count > ListCap), null);
    }

    /// <summary>Where each realized inflow lives — a received refund may be booked in a later month (ADR-V017).</summary>
    private async Task<Dictionary<Guid, Guid?>> InflowMonthsAsync(IEnumerable<Refund> rows, CancellationToken cancellationToken)
    {
        var ids = rows.Where(r => r.InflowTransactionId is not null).Select(r => r.InflowTransactionId!.Value).ToList();
        if (ids.Count == 0) return new();
        return await transactions.Query().Where(t => ids.Contains(t.Id))
            .Select(t => new { t.Id, t.MonthId })
            .ToDictionaryAsync(t => t.Id, t => (Guid?)t.MonthId, cancellationToken);
    }

    /// <summary>
    /// LEDGER-4: the household's own field, the notes. Replaced by what is sent and blank clears it; nothing else
    /// on the row moves, and the transaction's own re-derivation leaves it alone. Uniform 404 for a foreign id.
    /// </summary>
    public async Task<(RefundResponse? Refund, ErrorResponse? Error)> SetDetailsAsync(Guid id, UpdateRefundDetailsRequest request, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is null) return (null, new ErrorResponse("invalid_token", "No household on the token"));

        var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        if (notes?.Length > Refund.NotesMaxLength)
            return (null, new ErrorResponse("invalid_request", $"notes must be {Refund.NotesMaxLength} characters or fewer"));

        var refund = await refunds.Query().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (refund is null) return (null, new ErrorResponse("not_found", "refund not found"));

        refund.Notes = notes;
        refund.UpdatedAt = clock.GetUtcNow();
        refunds.Update(refund);
        await refunds.SaveChangesAsync(cancellationToken);
        return (RefundResponse.From(refund, source: await SourceAsync(refund, cancellationToken)), null);
    }

    public async Task<(RefundResponse? Refund, ErrorResponse? Error)> SetStatusAsync(Guid id, UpdateRefundStatusRequest request, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId) return (null, new ErrorResponse("invalid_token", "No household on the token"));
        if (RefundStatuses.Normalize(request.Status) is not { } next)
            return (null, new ErrorResponse("invalid_request", "status must be pending or received"));

        var refund = await refunds.Query().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (refund is null) return (null, new ErrorResponse("not_found", "refund not found"));
        var current = refund.Status;
        if (current == next) return (RefundResponse.From(refund, await InflowMonthAsync(refund, cancellationToken), await SourceAsync(refund, cancellationToken)), null); // idempotent

        var now = clock.GetUtcNow();
        DateOnly? receivedDate = null;
        if (next == RefundStatuses.Received)
        {
            // The day the money landed — dates the inflow and picks its month (ADR-V017). Unset = today.
            receivedDate = request.ReceivedDate ?? DateOnly.FromDateTime(now.UtcDateTime);
            if (receivedDate < refund.TransactionDate)
                return (null, new ErrorResponse("invalid_request", "received_date cannot be before the transaction date"));
        }

        await using var scope = await unitOfWork.BeginTransactionAsync(cancellationToken);

        Guid? inflowId = null;
        Guid? inflowMonthId = null;
        if (next == RefundStatuses.Received)
        {
            var source = await transactions.Query().FirstOrDefaultAsync(t => t.Id == refund.TransactionId, cancellationToken)
                ?? throw new InvalidOperationException("Source transaction not found for refund realization");
            var (month, staged) = await months.GetOrCreateForDateAsync(tenantId, receivedDate!.Value, cancellationToken);
            var inflow = new Transaction
            {
                TenantId = tenantId, MonthId = month.Id, BankId = source.BankId, CategoryId = source.CategoryId,
                Payee = refund.Payee, PaymentMethod = source.PaymentMethod, OriginalAmount = refund.AmountCrc, Currency = Currencies.Crc,
                TransactionDate = receivedDate.Value, AmountCrc = refund.AmountCrc, AmountUsd = refund.AmountUsd, ExchangeRateUsed = source.ExchangeRateUsed,
                TransactionType = TransactionTypes.Inflow, Source = TransactionSources.RefundRealization, CreatedAt = now, UpdatedAt = now,
            };
            await transactions.AddAsync(inflow, cancellationToken);
            try
            {
                await transactions.SaveChangesAsync(cancellationToken); // inside the scope — rolled back if the flip is lost
            }
            catch (DbUpdateException) when (staged.Count > 0)
            {
                // Another request created the received date's month first (unique TenantId/Year/MonthNumber). The
                // scope is already poisoned, so this is reported as the same "changed concurrently — retry" the
                // guarded flip uses; the retry finds the winner's month.
                transactions.Remove(inflow);
                months.Unstage(month, staged);
                logger.LogWarning("Lost the month-creation race booking refund {RefundId} into {Year}-{Month:00}", id, month.Year, month.MonthNumber);
                return (null, new ErrorResponse("refund_status_conflict", "The month for the received date was created concurrently — reload and retry"));
            }
            inflowId = inflow.Id;
            inflowMonthId = month.Id;
        }

        // The guarded flip: only the caller who still sees the old status wins (ADR-V014).
        var flipped = await refunds.Query()
            .Where(r => r.Id == id && r.Status == current)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, next)
                .SetProperty(r => r.InflowTransactionId, inflowId)
                .SetProperty(r => r.ReceivedDate, receivedDate)
                .SetProperty(r => r.UpdatedAt, now), cancellationToken);
        if (flipped != 1)
        {
            logger.LogWarning("Refund {RefundId} status changed concurrently; this flip lost", id);
            return (null, new ErrorResponse("refund_status_conflict", "Refund status was changed concurrently — reload and retry")); // scope disposes → rollback
        }

        if (next == RefundStatuses.Pending && refund.InflowTransactionId is { } previousInflowId
            && await transactions.Query().FirstOrDefaultAsync(t => t.Id == previousInflowId, cancellationToken) is { } realized)
        {
            transactions.Remove(realized);
            await months.RemoveIfEmptyAsync(realized.MonthId, [realized.Id], cancellationToken);
            await transactions.SaveChangesAsync(cancellationToken);
        }

        await scope.CommitAsync(cancellationToken);
        refund.Status = next; refund.InflowTransactionId = inflowId; refund.ReceivedDate = receivedDate; refund.UpdatedAt = now;
        logger.LogInformation("Refund {RefundId} marked {Status}", id, next);
        return (RefundResponse.From(refund, inflowMonthId, await SourceAsync(refund, cancellationToken)), null);
    }

    private async Task<Guid?> InflowMonthAsync(Refund refund, CancellationToken cancellationToken) =>
        refund.InflowTransactionId is { } inflowId ? (await InflowMonthsAsync([refund], cancellationToken)).GetValueOrDefault(inflowId) : null;
}
