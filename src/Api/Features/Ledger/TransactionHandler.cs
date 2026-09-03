using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Services;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Ledger;

/// <summary>
/// LEDGER-2: the transaction path (ADR-V005/V006/V007). Create: validate → resolve the rate → resolve
/// or stage the month → derive both amounts → freeze the rate → one <c>SaveChanges</c>. Everything is
/// checked <em>before</em> anything is staged, so a rejected request never leaves an empty month.
/// Update re-derives amounts from the frozen rate and re-resolves the month on a date change (the
/// emptied source month goes away). Delete removes the row and, when it was the month's last, the
/// month and its weeks. Derived rows (<c>source != manual</c>) are read-only here.
/// </summary>
public sealed class TransactionHandler(
    IRepository<Transaction> transactions,
    IRepository<Category> categories,
    IRepository<Bank> banks,
    IRepository<Envelope> envelopes,
    MonthHandler months,
    IExchangeRateResolver rates,
    ICurrentTenant tenant,
    TimeProvider clock,
    ILogger<TransactionHandler> logger)
{
    private const int MaxAttempts = 2; // one retry: a lost month-creation race finds the winner's month next time

    private sealed record Valid(string Payee, Guid BankId, string PaymentMethod, decimal Amount, string Currency, DateOnly Date, Guid CategoryId, string Type, Guid? EnvelopeId);

    public async Task<(TransactionResponse? Transaction, ErrorResponse? Error)> CreateAsync(CreateTransactionRequest r, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId) return (null, NoTenant());
        var (v, invalid) = await ValidateAsync(r.Payee, r.BankId, r.PaymentMethod, r.OriginalAmount, r.Currency, r.TransactionDate, r.CategoryId, r.TransactionType, r.EnvelopeId, cancellationToken);
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
                TransactionType = v.Type, Source = TransactionSources.Manual, CreatedAt = now, UpdatedAt = now,
            };
            await transactions.AddAsync(tx, cancellationToken);
            try
            {
                await transactions.SaveChangesAsync(cancellationToken); // month + weeks + transaction, atomically
                return (TransactionResponse.From(tx), null);
            }
            catch (DbUpdateException) when (staged.Count > 0 && attempt < MaxAttempts)
            {
                // Another request created the same month first (unique TenantId/Year/MonthNumber). Detach what
                // we staged and go again — get-or-create now finds the winner's month.
                transactions.Remove(tx);
                months.Unstage(month, staged);
                logger.LogWarning("Lost the month-creation race for {Year}-{Month:00}; retrying", month.Year, month.MonthNumber);
            }
        }
    }

    public async Task<(TransactionResponse? Transaction, ErrorResponse? Error)> UpdateAsync(Guid id, UpdateTransactionRequest r, CancellationToken cancellationToken)
    {
        if (tenant.TenantId is not { } tenantId) return (null, NoTenant());
        var (v, invalid) = await ValidateAsync(r.Payee, r.BankId, r.PaymentMethod, r.OriginalAmount, r.Currency, r.TransactionDate, r.CategoryId, r.TransactionType, r.EnvelopeId, cancellationToken);
        if (invalid is not null) return (null, invalid);

        var tx = await transactions.Query().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tx is null) return (null, NotFound());
        if (!TransactionSources.IsEditable(tx.Source)) return (null, Derived());

        // The rate is frozen (ADR-V006): amounts re-derive from it, never from a fresh quote.
        var (amountCrc, amountUsd) = CurrencyMath.DeriveAmounts(v!.Amount, v.Currency, tx.ExchangeRateUsed);
        var previousMonthId = tx.MonthId;

        for (var attempt = 1; ; attempt++)
        {
            var (month, staged) = v.Date == tx.TransactionDate
                ? ((await months.FindContainingAsync(v.Date, cancellationToken))!, (IReadOnlyList<Week>)[])
                : await months.GetOrCreateForDateAsync(tenantId, v.Date, cancellationToken);

            tx.Payee = v.Payee; tx.BankId = v.BankId; tx.PaymentMethod = v.PaymentMethod; tx.OriginalAmount = CurrencyMath.Round2(v.Amount);
            tx.Currency = v.Currency; tx.TransactionDate = v.Date; tx.CategoryId = v.CategoryId; tx.TransactionType = v.Type;
            tx.EnvelopeId = v.EnvelopeId; tx.MonthId = month.Id; tx.AmountCrc = amountCrc; tx.AmountUsd = amountUsd; tx.UpdatedAt = clock.GetUtcNow();
            transactions.Update(tx);

            if (month.Id != previousMonthId)
                await months.RemoveIfEmptyAsync(previousMonthId, tx.Id, cancellationToken); // a date fix that empties its old month deletes it

            try
            {
                await transactions.SaveChangesAsync(cancellationToken);
                return (TransactionResponse.From(tx), null);
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
        return tx is null ? null : TransactionResponse.From(tx);
    }

    /// <summary>Hard delete (no soft delete for money movement). Removes the month too when this was its last transaction.</summary>
    public async Task<ErrorResponse?> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var tx = await transactions.Query().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tx is null) return NotFound();
        if (!TransactionSources.IsEditable(tx.Source)) return Derived();

        transactions.Remove(tx);
        var monthGone = await months.RemoveIfEmptyAsync(tx.MonthId, tx.Id, cancellationToken);
        await transactions.SaveChangesAsync(cancellationToken);
        if (monthGone) logger.LogInformation("Month {MonthId} auto-deleted with its last transaction", tx.MonthId);
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

    /// <summary>Field rules shared by create and update (donor US-006/007 + ADR-V007), then the catalog references (must exist in the household and be active).</summary>
    private async Task<(Valid? Valid, ErrorResponse? Error)> ValidateAsync(
        string? payee, Guid? bankId, string? paymentMethod, decimal amount, string? currency, DateOnly? date,
        Guid? categoryId, string? type, Guid? envelopeId, CancellationToken cancellationToken)
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

        var isContribution = t == TransactionTypes.EnvelopeContribution;
        if (isContribution && envelopeId is null) return (null, Invalid("envelope_id is required for envelope_contribution transactions"));
        if (!isContribution && envelopeId is not null) return (null, Invalid("envelope_id is only valid for envelope_contribution transactions"));
        if (isContribution && method != PaymentMethods.BankAccount) return (null, Invalid("envelope contributions must use payment_method bank_account"));

        // Tenant-scoped lookups: another household's id simply does not exist.
        if (!await categories.Query().AnyAsync(c => c.Id == category && c.IsActive, cancellationToken)) return (null, Invalid("unknown or inactive category"));
        if (!await banks.Query().AnyAsync(b => b.Id == bank && b.IsActive, cancellationToken)) return (null, Invalid("unknown or inactive bank"));
        if (isContribution && !await envelopes.Query().AnyAsync(e => e.Id == envelopeId && e.IsActive, cancellationToken)) return (null, Invalid("unknown or inactive envelope"));

        return (new Valid(payee.Trim(), bank, method, amount, cur, d, category, t, isContribution ? envelopeId : null), null);
    }

    private static ErrorResponse Invalid(string message) => new("invalid_request", message);
    private static ErrorResponse NotFound() => new("not_found", "transaction not found");
    private static ErrorResponse Derived() => new("derived_transaction", "This transaction is derived from a refund — edit or delete it through the refund");
    private static ErrorResponse NoTenant() => new("invalid_token", "No household on the token");
}
