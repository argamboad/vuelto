using Vuelto.Core.Budget;

namespace Vuelto.Core.Entities;

/// <summary>
/// Money movement captured in both currencies at a frozen rate (ADR-V004/V006/V007). The date decides
/// the <see cref="MonthId"/> through the anchor window; <see cref="ExchangeRateUsed"/> is set at
/// creation and never recalculated — edits re-derive the two amounts from it. Bank and category are
/// required; an envelope is required exactly for <c>envelope_contribution</c> rows.
/// </summary>
public class Transaction : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid MonthId { get; set; }
    public Guid BankId { get; set; }
    public Guid CategoryId { get; set; }
    public Guid? EnvelopeId { get; set; }
    public required string Payee { get; set; }
    public string PaymentMethod { get; set; } = PaymentMethods.CreditCard;
    public decimal OriginalAmount { get; set; }
    public string Currency { get; set; } = Currencies.Crc;
    public DateOnly TransactionDate { get; set; }
    public decimal AmountCrc { get; set; }
    public decimal AmountUsd { get; set; }
    public decimal ExchangeRateUsed { get; set; }
    public string TransactionType { get; set; } = TransactionTypes.Budgeted;
    public string Source { get; set; } = TransactionSources.Manual;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
