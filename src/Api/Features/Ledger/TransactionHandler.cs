using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Ledger;

/// <summary>
/// LEDGER-2/3: the transaction path (ADR-V005/V006/V007). Create: validate → resolve the rate → resolve
/// or stage the month → derive both amounts → freeze the rate → sync the expected refund → one
/// <c>SaveChanges</c>. Everything is checked <em>before</em> anything is staged, so a rejected request
/// never leaves an empty month. Update re-derives amounts from the frozen rate, re-resolves the month
/// on a date change (the emptied source month goes away) and re-derives / removes the refund — a
/// realized refund's inflow follows the re-derived amounts. Delete removes the row, its refund, the
/// refund's realized inflow, and any month left empty. Derived rows (<c>source != manual</c>) are
/// read-only here.
/// </summary>
public sealed class TransactionHandler(
    IRepository<Transaction> transactions,
    IRepository<Refund> refunds,
    IRepository<Category> categories,
    IRepository<Bank> banks,
    IRepository<Envelope> envelopes,
    MonthHandler months,
    IExchangeRateResolver rates,
    ICurrentTenant tenant,
    TimeProvider clock,
    ILogger<TransactionHandler> logger) : ITransactionService
{
    private const int MaxAttempts = 2; // one retry: a lost month-creation race finds the winner's month next time

    private sealed record Valid(string Payee, Guid BankId, string PaymentMethod, decimal Amount, string Currency, DateOnly Date, Guid CategoryId, string Type, Guid? EnvelopeId, decimal? RefundPercentage);

    /// <summary>The manual create (LEDGER-2): <c>source = manual</c>.</summary>
    public Task<(TransactionResponse? Transaction, ErrorResponse? Error)> CreateAsync(CreateTransactionRequest r, CancellationToken cancellationToken) =>
        CreateAsync(new CreateTransactionCommand(r.Payee, r.BankId, r.PaymentMethod, r.OriginalAmount, r.Currency, r.TransactionDate, r.CategoryId, r.TransactionType, r.ExchangeRate, r.EnvelopeId, r.RefundExpected, r.RefundPercentage), cancellationToken);

    /// <summary>
    /// The Core contract (ADR-V010): the same create for another slice's caller — the review queue books a
    /// confirmed voucher through here with <c>source = email</c>, inside its own unit-of-work scope.
    /// </summary>
    async Task<(TransactionCreated? Transaction, TransactionError? Error)> ITransactionService.CreateAsync(CreateTransactionCommand command, CancellationToken cancellationToken)
    {
        var (tx, error) = await CreateAsync(command, cancellationToken);
        return tx is null
            ? (null, new TransactionError(error!.Error, error.Message))
            : (new TransactionCreated(tx.Id, tx.MonthId, tx.AmountCrc, tx.AmountUsd, tx.ExchangeRateUsed, tx.Source), null);
    }

    private async Task<(TransactionResponse? Transaction, ErrorResponse? Error)> CreateAsync(CreateTransactionCommand r, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId) return (null, NoTenant());
        var (v, invalid) = await ValidateAsync(r.Payee, r.BankId, r.PaymentMethod, r.OriginalAmount, r.Currency, r.TransactionDate, r.CategoryId, r.TransactionType, r.EnvelopeId, r.RefundExpected, r.RefundPercentage, cancellationToken);
        if (invalid is not null) return (null, invalid);
        if (r.ExchangeRate is <= 0) return (null, Invalid("exchange_rate must be positive"));

        // The rate is settled before any write (ADR-V006): a manual override wins, else the chain; nothing → block.
        decimal rate;
        if (r.ExchangeRate is { } given) rate = given;
        else if (await rates.ResolveAsync(cancellationToken) is { } resolved) rate = resolved.Rate;
        else return (null, new ErrorResponse("exchange_rate_unavailable", "No exchange rate available — try again later or enter one manually"));

        var (amountCrc, amountUsd) = CurrencyMath.DeriveAmounts(v!.Amount, v.Currency, rate);
        var now = clock.GetUtcNow();

        for (var attempt = 1; ; attempt++)
        {
            var (month, staged) = await months.GetOrCreateForDateAsync(tenantId, v.Date, cancellationToken);
            var tx = new Transaction
            {
                TenantId = tenantId, MonthId = month.Id, BankId = v.BankId, CategoryId = v.CategoryId, EnvelopeId = v.EnvelopeId,
                Payee = v.Payee, PaymentMethod = v.PaymentMethod, OriginalAmount = CurrencyMath.Round2(v.Amount), Currency = v.Currency,
                TransactionDate = v.Date, AmountCrc = amountCrc, AmountUsd = amountUsd, ExchangeRateUsed = rate,
                TransactionType = v.Type, Source = r.Source, CreatedAt = now, UpdatedAt = now,
            };
            await transactions.AddAsync(tx, cancellationToken);
            var (refund, _) = await SyncRefundAsync(tx, v.RefundPercentage, now, cancellationToken); // a new row has nothing to remove
            try
            {
                await transactions.SaveChangesAsync(cancellationToken); // month + weeks + transaction (+ refund), atomically
                return (TransactionResponse.From(tx, refund), null);
            }
            catch (DbUpdateException) when (staged.Count > 0 && attempt < MaxAttempts)
            {
                // Another request created the same month first (unique TenantId/Year/MonthNumber). Detach what
                // we staged and go again — get-or-create now finds the winner's month.
                transactions.Remove(tx);
                if (refund is not null) refunds.Remove(refund);
                months.Unstage(month, staged);
                logger.LogWarning("Lost the month-creation race for {Year}-{Month:00}; retrying", month.Year, month.MonthNumber);
            }
        }
    }

    public async Task<(TransactionResponse? Transaction, ErrorResponse? Error)> UpdateAsync(Guid id, UpdateTransactionRequest r, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId) return (null, NoTenant());
        var (v, invalid) = await ValidateAsync(r.Payee, r.BankId, r.PaymentMethod, r.OriginalAmount, r.Currency, r.TransactionDate, r.CategoryId, r.TransactionType, r.EnvelopeId, r.RefundExpected, r.RefundPercentage, cancellationToken);
        if (invalid is not null) return (null, invalid);

        var tx = await transactions.Query().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tx is null) return (null, NotFound());
        if (!TransactionSources.IsEditable(tx.Source)) return (null, Derived());

        // The rate is frozen (ADR-V006): amounts re-derive from it, never from a fresh quote.
        var (amountCrc, amountUsd) = CurrencyMath.DeriveAmounts(v!.Amount, v.Currency, tx.ExchangeRateUsed);
        var previousMonthId = tx.MonthId;
        var now = clock.GetUtcNow();

        for (var attempt = 1; ; attempt++)
        {
            var (month, staged) = v.Date == tx.TransactionDate
                ? ((await months.FindContainingAsync(v.Date, cancellationToken))!, (IReadOnlyList<Week>)[])
                : await months.GetOrCreateForDateAsync(tenantId, v.Date, cancellationToken);

            tx.Payee = v.Payee; tx.BankId = v.BankId; tx.PaymentMethod = v.PaymentMethod; tx.OriginalAmount = CurrencyMath.Round2(v.Amount);
            tx.Currency = v.Currency; tx.TransactionDate = v.Date; tx.CategoryId = v.CategoryId; tx.TransactionType = v.Type;
            tx.EnvelopeId = v.EnvelopeId; tx.MonthId = month.Id; tx.AmountCrc = amountCrc; tx.AmountUsd = amountUsd; tx.UpdatedAt = now;
            transactions.Update(tx);

            var (refund, removedInflow) = await SyncRefundAsync(tx, v.RefundPercentage, now, cancellationToken);

            // Month cleanup (ADR-V005): a month left with no row other than the ones leaving it goes away. "Leaving"
            // is exactly the set this save removes or moves — the transaction only when its date moved it, plus a
            // realized inflow that a dropped refund takes with it. Excluding anything else would delete a live month.
            var leaving = new List<Guid>();
            var touched = new HashSet<Guid>();
            if (month.Id != previousMonthId) { leaving.Add(tx.Id); touched.Add(previousMonthId); }
            if (removedInflow is not null) { leaving.Add(removedInflow.Id); touched.Add(removedInflow.MonthId); }
            foreach (var monthId in touched)
                await months.RemoveIfEmptyAsync(monthId, leaving, cancellationToken);

            try
            {
                await transactions.SaveChangesAsync(cancellationToken);
                return (TransactionResponse.From(tx, refund), null);
            }
            catch (DbUpdateException) when (staged.Count > 0 && attempt < MaxAttempts)
            {
                months.Unstage(month, staged);
                logger.LogWarning("Lost the month-creation race while moving transaction {Id}; retrying", id);
            }
        }
    }

    public async Task<TransactionResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var tx = await transactions.Query().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tx is null) return null;
        var refund = await refunds.Query().FirstOrDefaultAsync(x => x.TransactionId == id, cancellationToken);
        return TransactionResponse.From(tx, refund);
    }

    /// <summary>Hard delete (no soft delete for money movement). Takes the refund, its realized inflow, and any month left empty along.</summary>
    public async Task<ErrorResponse?> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var tx = await transactions.Query().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tx is null) return NotFound();
        if (!TransactionSources.IsEditable(tx.Source)) return Derived();

        var leaving = new List<Guid> { tx.Id };
        var touchedMonths = new HashSet<Guid> { tx.MonthId };
        if (await refunds.Query().FirstOrDefaultAsync(x => x.TransactionId == tx.Id, cancellationToken) is { } refund)
        {
            if (await RemoveRealizedInflowAsync(refund, cancellationToken) is { } inflow) { leaving.Add(inflow.Id); touchedMonths.Add(inflow.MonthId); }
            refunds.Remove(refund);
        }
        transactions.Remove(tx);

        var monthsGone = 0;
        foreach (var monthId in touchedMonths)
            if (await months.RemoveIfEmptyAsync(monthId, leaving, cancellationToken)) monthsGone++;
        await transactions.SaveChangesAsync(cancellationToken);
        if (monthsGone > 0) logger.LogInformation("{Count} month(s) auto-deleted with their last transaction", monthsGone);
        return null;
    }

    /// <summary>The month's rows newest first with catalog names (all states — inactive names still label history). Null = month not found.</summary>
    public async Task<IReadOnlyList<TransactionListItemResponse>?> ListForMonthAsync(Guid monthId, CancellationToken cancellationToken)
    {
        if (await months.GetAsync(monthId, cancellationToken) is null) return null;

        var rows = await transactions.Query()
            .Where(t => t.MonthId == monthId)
            .OrderByDescending(t => t.TransactionDate).ThenByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);
        var categoryNames = await categories.Query().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var bankNames = await banks.Query().ToDictionaryAsync(b => b.Id, b => b.Name, cancellationToken);

        return rows.Select(t => new TransactionListItemResponse(
            t.Id, t.Payee, t.TransactionDate,
            categoryNames.GetValueOrDefault(t.CategoryId), bankNames.GetValueOrDefault(t.BankId),
            t.PaymentMethod, t.TransactionType, t.AmountCrc, t.AmountUsd, t.Source)).ToList();
    }

    /// <summary>
    /// LEDGER-3: keeps the transaction's expected refund in step (staged, not saved). A percentage on an
    /// unplanned-essential row creates or re-derives the refund (amounts = % × the frozen amounts) —
    /// and a realized refund's inflow tracks the re-derived amounts, keeping its own month and date.
    /// Any other class, or no percentage, removes an existing refund together with its realized inflow.
    /// Returns the refund now attached to the transaction (or null) and the realized inflow it removed (or
    /// null) — the caller owns the month cleanup, because only it knows which rows are leaving which month.
    /// </summary>
    private async Task<(Refund? Refund, Transaction? RemovedInflow)> SyncRefundAsync(Transaction tx, decimal? percentage, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await refunds.Query().FirstOrDefaultAsync(x => x.TransactionId == tx.Id, cancellationToken);
        var applies = tx.TransactionType == TransactionTypes.UnplannedEssential && percentage is > 0;

        if (!applies)
        {
            if (existing is null) return (null, null);
            var inflow = await RemoveRealizedInflowAsync(existing, cancellationToken);
            refunds.Remove(existing);
            return (null, inflow);
        }

        var pct = percentage!.Value;
        var amountCrc = CurrencyMath.Round2(tx.AmountCrc * pct / 100m);
        var amountUsd = CurrencyMath.Round2(tx.AmountUsd * pct / 100m);

        if (existing is null)
        {
            var refund = new Refund
            {
                TenantId = tx.TenantId, MonthId = tx.MonthId, TransactionId = tx.Id, Payee = tx.Payee, TransactionDate = tx.TransactionDate,
                Percentage = pct, AmountCrc = amountCrc, AmountUsd = amountUsd, Status = RefundStatuses.Pending, CreatedAt = now, UpdatedAt = now,
            };
            await refunds.AddAsync(refund, cancellationToken);
            return (refund, null);
        }

        existing.MonthId = tx.MonthId; existing.Payee = tx.Payee; existing.TransactionDate = tx.TransactionDate;
        existing.Percentage = pct; existing.AmountCrc = amountCrc; existing.AmountUsd = amountUsd; existing.UpdatedAt = now;
        refunds.Update(existing);

        if (existing.InflowTransactionId is { } inflowId
            && await transactions.Query().FirstOrDefaultAsync(t => t.Id == inflowId, cancellationToken) is { } realized)
        {
            realized.OriginalAmount = amountCrc; realized.AmountCrc = amountCrc; realized.AmountUsd = amountUsd; realized.UpdatedAt = now; // stored in CRC
            transactions.Update(realized);
        }
        return (existing, null);
    }

    /// <summary>Stages the removal of a realized refund's inflow (if any) and returns it, so the caller can also retire its month.</summary>
    private async Task<Transaction?> RemoveRealizedInflowAsync(Refund refund, CancellationToken cancellationToken)
    {
        if (refund.InflowTransactionId is not { } inflowId) return null;
        var inflow = await transactions.Query().FirstOrDefaultAsync(t => t.Id == inflowId, cancellationToken);
        if (inflow is null) return null;
        refund.InflowTransactionId = null;
        transactions.Remove(inflow);
        return inflow;
    }

    /// <summary>Field rules shared by create and update (donor US-006/007/012 + ADR-V007), then the catalog references (must exist in the household and be active).</summary>
    private async Task<(Valid? Valid, ErrorResponse? Error)> ValidateAsync(
        string? payee, Guid? bankId, string? paymentMethod, decimal amount, string? currency, DateOnly? date,
        Guid? categoryId, string? type, Guid? envelopeId, bool refundExpected, decimal? refundPercentage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payee)) return (null, Invalid("payee is required"));
        if (payee.Trim().Length > 200) return (null, Invalid("payee must be 200 characters or fewer"));
        if (amount <= 0) return (null, Invalid("original_amount must be greater than zero"));
        if (Currencies.Normalize(currency) is not { } cur) return (null, Invalid("currency must be CRC or USD"));
        if (date is not { } d) return (null, Invalid("transaction_date is required"));
        if (TransactionTypes.Normalize(type) is not { } t) return (null, Invalid($"transaction_type must be one of: {string.Join(", ", TransactionTypes.All)}"));
        if (PaymentMethods.Normalize(paymentMethod) is not { } method) return (null, Invalid("payment_method must be credit_card or bank_account"));
        if (bankId is not { } bank) return (null, Invalid("bank_id is required (every transaction names its money source)"));
        if (categoryId is not { } category) return (null, Invalid("category_id is required"));
        if (refundExpected && refundPercentage is null or <= 0 or > 100) return (null, Invalid("refund_percentage must be between 0 and 100 when refund_expected is set"));

        var isContribution = t == TransactionTypes.EnvelopeContribution;
        if (isContribution && envelopeId is null) return (null, Invalid("envelope_id is required for envelope_contribution transactions"));
        if (!isContribution && envelopeId is not null) return (null, Invalid("envelope_id is only valid for envelope_contribution transactions"));
        if (isContribution && method != PaymentMethods.BankAccount) return (null, Invalid("envelope contributions must use payment_method bank_account"));

        // Tenant-scoped lookups: another household's id simply does not exist.
        if (!await categories.Query().AnyAsync(c => c.Id == category && c.IsActive, cancellationToken)) return (null, Invalid("unknown or inactive category"));
        if (!await banks.Query().AnyAsync(b => b.Id == bank && b.IsActive, cancellationToken)) return (null, Invalid("unknown or inactive bank"));
        if (isContribution && !await envelopes.Query().AnyAsync(e => e.Id == envelopeId && e.IsActive, cancellationToken)) return (null, Invalid("unknown or inactive envelope"));

        // The refund flag only means something on an unplanned essential (donor US-012); elsewhere it is ignored, never an error.
        var pct = refundExpected && t == TransactionTypes.UnplannedEssential ? refundPercentage : null;
        return (new Valid(payee.Trim(), bank, method, amount, cur, d, category, t, isContribution ? envelopeId : null, pct), null);
    }

    private static ErrorResponse Invalid(string message) => new("invalid_request", message);
    private static ErrorResponse NotFound() => new("not_found", "transaction not found");
    private static ErrorResponse Derived() => new("derived_transaction", "This transaction is derived from a refund — edit or delete it through the refund");
    private static ErrorResponse NoTenant() => new("invalid_token", "No household on the token");
}
