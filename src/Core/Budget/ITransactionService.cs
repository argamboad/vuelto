namespace Vuelto.Core.Budget;

/// <summary>
/// The one way to book a transaction from outside the Ledger slice (ADR-V010: confirm is the only
/// draft → transaction path and it runs through the <i>same</i> create as manual entry — month
/// auto-create, rate resolve-and-freeze, refund sync, validation, all inherited). Feature slices may not
/// reference each other (R7), so the Ledger handler implements this Core contract and the review queue
/// depends on the contract. Every write happens inside the caller's ambient unit-of-work scope when one
/// is open, so a caller can pair the create with its own conditional flip atomically.
/// </summary>
public interface ITransactionService
{
    /// <summary>
    /// Validates, resolves the rate, resolves or stages the month, and saves the transaction. Returns the
    /// created row or an error — <c>invalid_request</c> (nothing written), <c>exchange_rate_unavailable</c>
    /// (nothing written), <c>invalid_token</c> (no household on the caller).
    /// </summary>
    Task<(TransactionCreated? Transaction, TransactionError? Error)> CreateAsync(CreateTransactionCommand command, CancellationToken cancellationToken = default);
}

/// <summary>What a caller supplies to book a transaction; the same fields as the manual create, plus the provenance.</summary>
public sealed record CreateTransactionCommand(
    string? Payee,
    Guid? BankId,
    string? PaymentMethod,
    decimal OriginalAmount,
    string? Currency,
    DateOnly? TransactionDate,
    Guid? CategoryId,
    string? TransactionType,
    decimal? ExchangeRate = null,
    Guid? EnvelopeId = null,
    bool RefundExpected = false,
    decimal? RefundPercentage = null,
    string Source = TransactionSources.Manual);

public sealed record TransactionCreated(Guid Id, Guid MonthId, decimal AmountCrc, decimal AmountUsd, decimal ExchangeRateUsed, string Source);

public sealed record TransactionError(string Error, string Message);
