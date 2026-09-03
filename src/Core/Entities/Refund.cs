using Vuelto.Core.Budget;

namespace Vuelto.Core.Entities;

/// <summary>
/// An expected refund <b>derived</b> from an <c>unplanned_essential</c> transaction flagged "refund
/// expected" with a percentage (ADR-V007): its amounts are that percentage of the transaction's frozen
/// amounts, so it inherits the frozen rate. Created, re-derived and removed by the transaction's own
/// create / update / delete — only <see cref="Status"/> is edited directly. Informational until it
/// lands: flipping to <c>received</c> books a derived <c>inflow</c> transaction
/// (<see cref="InflowTransactionId"/>, <c>source = refund_realization</c>); flipping back removes it.
/// </summary>
public class Refund : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid MonthId { get; set; }

    /// <summary>The unplanned-essential transaction this refund is expected against (one refund per transaction).</summary>
    public Guid TransactionId { get; set; }

    public required string Payee { get; set; }
    public DateOnly TransactionDate { get; set; }

    /// <summary>Percentage of the transaction amount expected back (0 &lt; p ≤ 100).</summary>
    public decimal Percentage { get; set; }

    public decimal AmountCrc { get; set; }
    public decimal AmountUsd { get; set; }

    /// <summary>One of <see cref="RefundStatuses"/>.</summary>
    public string Status { get; set; } = RefundStatuses.Pending;

    /// <summary>The realized inflow, present ⇔ <see cref="Status"/> is <c>received</c>.</summary>
    public Guid? InflowTransactionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
