using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

/// <summary>
/// One set of rules for both budget-line tables: unique name per household (case-insensitivity in the
/// handler), RESTRICT FKs to the soft-deleted catalogs (a line keeps naming history), NUMERIC(12,2)
/// budgets (ADR-V004), and an index for the ordered list. EF discovers only the two sealed subclasses.
/// </summary>
public abstract class ExpenseLineConfiguration<T> : IEntityTypeConfiguration<T> where T : class, IExpenseLine
{
    public void Configure(EntityTypeBuilder<T> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.PaymentMethod).HasMaxLength(32).IsRequired();
        b.Property(x => x.BudgetCrc).HasPrecision(12, 2);
        b.Property(x => x.BudgetUsd).HasPrecision(12, 2);
        b.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Bank>().WithMany().HasForeignKey(x => x.BankId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.SortOrder });
    }
}

public sealed class FixedExpenseConfiguration : ExpenseLineConfiguration<FixedExpense>;

public sealed class VariableExpenseConfiguration : ExpenseLineConfiguration<VariableExpense>;
