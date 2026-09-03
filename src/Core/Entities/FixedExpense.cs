using Vuelto.Core.Budget;

namespace Vuelto.Core.Entities;

/// <summary>A recurring budget line (mortgage, subscriptions…) — see <see cref="IExpenseLine"/>.</summary>
public class FixedExpense : IExpenseLine
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public decimal BudgetCrc { get; set; }
    public decimal BudgetUsd { get; set; }
    public string PaymentMethod { get; set; } = PaymentMethods.CreditCard;
    public int SortOrder { get; set; }
    public Guid CategoryId { get; set; }
    public Guid? BankId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
