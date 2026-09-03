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
/// transaction's bank and category, <c>source = refund_realization</c>) and links it; <c>received →
/// pending</c> removes it (and its month if emptied). The flip is a <b>conditional update</b> inside a
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
    /// <summary>The month's refunds newest first. Null = month not found (uniform 404).</summary>
    public async Task<IReadOnlyList<RefundResponse>?> ListForMonthAsync(Guid monthId, CancellationToken cancellationToken)
    {
        if (await months.GetAsync(monthId, cancellationToken) is null) return null;
        var rows = await refunds.Query().Where(r => r.MonthId == monthId)
            .OrderByDescending(r => r.TransactionDate).ThenByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);
        return rows.Select(RefundResponse.From).ToList();
    }

    public async Task<(RefundResponse? Refund, ErrorResponse? Error)> SetStatusAsync(Guid id, UpdateRefundStatusRequest request, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId) return (null, new ErrorResponse("invalid_token", "No household on the token"));
        if (RefundStatuses.Normalize(request.Status) is not { } next)
            return (null, new ErrorResponse("invalid_request", "status must be pending or received"));

        var refund = await refunds.Query().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (refund is null) return (null, new ErrorResponse("not_found", "refund not found"));
        var current = refund.Status;
        if (current == next) return (RefundResponse.From(refund), null); // idempotent

        var now = clock.GetUtcNow();
        await using var scope = await unitOfWork.BeginTransactionAsync(cancellationToken);

        Guid? inflowId = null;
        if (next == RefundStatuses.Received)
        {
            var source = await transactions.Query().FirstOrDefaultAsync(t => t.Id == refund.TransactionId, cancellationToken)
                ?? throw new InvalidOperationException("Source transaction not found for refund realization");
            var inflow = new Transaction
            {
                TenantId = tenantId, MonthId = refund.MonthId, BankId = source.BankId, CategoryId = source.CategoryId,
                Payee = refund.Payee, PaymentMethod = source.PaymentMethod, OriginalAmount = refund.AmountCrc, Currency = Currencies.Crc,
                TransactionDate = refund.TransactionDate, AmountCrc = refund.AmountCrc, AmountUsd = refund.AmountUsd, ExchangeRateUsed = source.ExchangeRateUsed,
                TransactionType = TransactionTypes.Inflow, Source = TransactionSources.RefundRealization, CreatedAt = now, UpdatedAt = now,
            };
            await transactions.AddAsync(inflow, cancellationToken);
            await transactions.SaveChangesAsync(cancellationToken); // inside the scope — rolled back if the flip is lost
            inflowId = inflow.Id;
        }

        // The guarded flip: only the caller who still sees the old status wins (ADR-V014).
        var flipped = await refunds.Query()
            .Where(r => r.Id == id && r.Status == current)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, next)
                .SetProperty(r => r.InflowTransactionId, inflowId)
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
        refund.Status = next; refund.InflowTransactionId = inflowId; refund.UpdatedAt = now;
        logger.LogInformation("Refund {RefundId} marked {Status}", id, next);
        return (RefundResponse.From(refund), null);
    }
}
