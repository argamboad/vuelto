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
    ITransactionService transactions,
    MerchantMappingHandler mappings,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<PendingVoucherHandler> logger)
{
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
            Source: TransactionSources.Email);

        // One boundary: create + guarded flip commit or roll back together (donor US-033 AC1).
        await using var scope = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var (created, error) = await transactions.CreateAsync(command, cancellationToken);
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
