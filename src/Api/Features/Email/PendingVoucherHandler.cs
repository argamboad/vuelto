using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;
using Vuelto.Core.Vouchers;

namespace Vuelto.Api.Features.Email;

/// <summary>
/// EMAIL-6: the review queue — the <b>only</b> path from a staged draft to a real transaction (ADR-V010,
/// donor US-030/US-033). Confirm builds the create from the draft plus the user's category and class (and
/// any override), books it through the Core <see cref="ITransactionService"/> — the same create as manual
/// entry: month auto-create, live rate resolve-and-freeze, refund sync, validation — with
/// <c>source = email</c>, then flips the draft <c>pending → confirmed</c> with a <b>conditional update</b>,
/// all inside one unit-of-work scope: a validation or rate failure writes nothing and the draft stays
/// pending; a concurrent second confirm loses the flip (0 rows), its transaction rolls back, and it gets
/// <c>not_pending</c> — exactly one transaction ever exists. Discard is the same conditional flip to
/// <c>discarded</c>, so it cannot revert a draft a concurrent confirm just committed. The dedup tombstone is
/// untouched by both. Reads are tenant-filtered: a foreign id is a uniform 404.
/// </summary>
public sealed class PendingVoucherHandler(
    IRepository<PendingVoucher> pendingVouchers,
    IRepository<IngestedVoucher> ingestedVouchers,
    IRepository<EmailConnection> connections,
    ITransactionService transactions,
    MerchantMappingHandler mappings,
    ICardResolver cards,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<PendingVoucherHandler> logger)
{
    /// <summary>
    /// EMAIL-7 — the queue reset (owner request, 2026-09-10): every draft still waiting disappears, its dedup
    /// tombstone with it, and each inbox that staged one has its cursor pulled back to just before the oldest
    /// of them, so the next sync reads those emails again. Confirmed and discarded drafts keep their tombstones,
    /// so nothing you already booked or threw away can come back. Transactions are never touched. One scope:
    /// drafts, tombstones and cursors commit together or not at all. Without <c>confirm</c> it is a 409.
    /// </summary>
    public async Task<(ClearQueueResponse? Result, ErrorResponse? Error)> ClearPendingAsync(bool confirm, CancellationToken cancellationToken)
    {
        if (!confirm) return (null, new ErrorResponse("confirmation_required", "Set confirm to clear the review queue."));

        var pending = await pendingVouchers.Query()
            .Where(v => v.Status == PendingVoucherStatuses.Pending)
            .Select(v => new { v.Id, v.EmailConnectionId, v.ReceivedAt })
            .ToListAsync(cancellationToken);
        if (pending.Count == 0) return (new ClearQueueResponse(0, 0), null);

        await using var scope = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var ids = pending.Select(p => p.Id).ToList();
        await ingestedVouchers.Query().Where(i => ids.Contains(i.PendingVoucherId)).ExecuteDeleteAsync(cancellationToken);
        var cleared = await pendingVouchers.Query().Where(v => ids.Contains(v.Id) && v.Status == PendingVoucherStatuses.Pending).ExecuteDeleteAsync(cancellationToken);

        // The connection is user-keyed (ADR-V002) and may belong to another member of the household — rewinding
        // it is still right: it staged these drafts, and the rewind can only bring back what we just cleared
        // (every other tombstone survives). Never move a cursor forward, and never behind the user's import_from.
        var rewound = 0;
        foreach (var group in pending.GroupBy(p => p.EmailConnectionId))
        {
            // ReceivedAt is nullable (a draft staged before the reader recorded it); with no arrival time at
            // all there is nothing to aim the cursor at, so that inbox keeps its cursor.
            var arrivals = group.Where(p => p.ReceivedAt is not null).Select(p => p.ReceivedAt!.Value).ToList();
            if (arrivals.Count == 0) continue;
            var connection = await connections.Query().FirstOrDefaultAsync(c => c.Id == group.Key, cancellationToken);
            if (connection is null) continue; // the inbox was disconnected since; nothing to rewind

            var target = arrivals.Min().AddMinutes(-1);
            if (target < connection.ImportFrom) target = connection.ImportFrom;
            if (connection.LastPolledAt is { } current && current <= target) continue;

            connection.LastPolledAt = target;
            connection.UpdatedAt = clock.GetUtcNow();
            connections.Update(connection);
            rewound++;
        }

        await connections.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
        logger.LogInformation("Review queue cleared: {Cleared} draft(s) removed, {Rewound} inbox cursor(s) rewound", cleared, rewound);
        return (new ClearQueueResponse(cleared, rewound), null);
    }

    /// <summary>Pending drafts, newest mail first.</summary>
    public async Task<IReadOnlyList<PendingVoucherResponse>> ListPendingAsync(CancellationToken cancellationToken)
    {
        var rows = await pendingVouchers.Query()
            .Where(v => v.Status == PendingVoucherStatuses.Pending)
            .OrderByDescending(v => v.ReceivedAt ?? v.CreatedAt).ThenByDescending(v => v.CreatedAt)
            .ToListAsync(cancellationToken);
        return rows.Select(PendingVoucherResponse.From).ToList();
    }

    public Task<int> CountPendingAsync(CancellationToken cancellationToken) =>
        pendingVouchers.Query().CountAsync(v => v.Status == PendingVoucherStatuses.Pending, cancellationToken);

    public async Task<(ConfirmVoucherResponse? Confirmed, ErrorResponse? Error)> ConfirmAsync(Guid id, ConfirmVoucherRequest r, CancellationToken cancellationToken)
    {
        var voucher = await pendingVouchers.Query().AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, cancellationToken);
        if (voucher is null) return (null, NotFound());
        if (voucher.Status != PendingVoucherStatuses.Pending) return (null, NotPending());

        // The user's decisions are validated here; everything about the transaction itself is the ledger's call.
        if (r.CategoryId is not { } categoryId || categoryId == Guid.Empty) return (null, Invalid("category_id is required"));
        if (!SuggestibleClasses.TryNormalize(r.TransactionClass, out var cls) || cls is null)
            return (null, Invalid($"transaction_class must be one of: {string.Join(", ", SuggestibleClasses.All)}"));

        var command = new CreateTransactionCommand(
            Payee: string.IsNullOrWhiteSpace(r.Payee) ? voucher.Merchant : r.Payee,
            BankId: r.BankId ?? voucher.BankId,
            PaymentMethod: r.PaymentMethod,
            OriginalAmount: r.OriginalAmount ?? voucher.Amount ?? 0m,
            Currency: r.Currency ?? voucher.Currency,
            TransactionDate: r.TransactionDate ?? voucher.Date,
            CategoryId: categoryId,
            TransactionType: cls,
            ExchangeRate: null, // resolve + freeze the live rate, like manual entry (ADR-V006)
            RefundExpected: r.RefundExpected, // the ledger validates the percentage and spawns the refund (LEDGER-3)
            RefundPercentage: r.RefundPercentage,
            Source: TransactionSources.Email,
            Notes: r.Notes);

        // One boundary: create + guarded flip commit or roll back together (donor US-033 AC1).
        await using var scope = await unitOfWork.BeginTransactionAsync(cancellationToken);

        // CARDS-1: the card the voucher printed — found, or created as BRAND-1234 on first sight — rides on the transaction.
        var cardId = await cards.ResolveOrCreateAsync(voucher.CardBrand, voucher.CardNumber, r.BankId ?? voucher.BankId, cancellationToken);
        var (created, error) = await transactions.CreateAsync(command with { CardId = cardId }, cancellationToken);
        if (created is null) return (null, new ErrorResponse(error!.Error, error.Message)); // nothing written; the draft stays pending

        var now = clock.GetUtcNow();
        var flipped = await pendingVouchers.Query()
            .Where(v => v.Id == id && v.Status == PendingVoucherStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.Status, PendingVoucherStatuses.Confirmed)
                .SetProperty(v => v.ConfirmedTransactionId, created.Id)
                .SetProperty(v => v.UpdatedAt, now), cancellationToken);
        if (flipped != 1)
        {
            logger.LogWarning("Pending voucher {Id} was actioned concurrently; this confirm lost and its transaction rolls back", id);
            return (null, NotPending()); // scope disposes without commit → the just-created transaction (and any new month) is gone
        }

        await scope.CommitAsync(cancellationToken);
        logger.LogInformation("Pending voucher {Id} confirmed → transaction {TransactionId}", id, created.Id);

        // Learn-on-confirm runs after the commit — non-critical, never undoes a confirm, never overwrites a rule.
        var remembered = r.RememberMerchant && await mappings.RememberAsync(voucher.Merchant, categoryId, cls, cancellationToken);
        return (new ConfirmVoucherResponse(created.Id, created.MonthId, created.AmountCrc, created.AmountUsd, remembered), null);
    }

    public async Task<ErrorResponse?> DiscardAsync(Guid id, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var flipped = await pendingVouchers.Query()
            .Where(v => v.Id == id && v.Status == PendingVoucherStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.Status, PendingVoucherStatuses.Discarded)
                .SetProperty(v => v.UpdatedAt, now), cancellationToken);
        if (flipped == 1) return null;
        // 0 rows: either it never existed here (uniform 404) or a concurrent confirm/discard already actioned it (409).
        return await pendingVouchers.Query().AnyAsync(v => v.Id == id, cancellationToken) ? NotPending() : NotFound();
    }

    private static ErrorResponse Invalid(string message) => new("invalid_request", message);
    private static ErrorResponse NotFound() => new("not_found", "pending voucher not found");
    private static ErrorResponse NotPending() => new("not_pending", "This voucher is no longer pending");
}
