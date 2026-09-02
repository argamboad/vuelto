using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

/// <summary>
/// One budget-settings row per household: the unique index on <c>TenantId</c> is the invariant
/// (ADR-V003). Money columns use the app-wide <c>NUMERIC(12,2)</c> (ADR-V004). ITenantScoped, so the
/// global query filter + RLS policy (shipped in the same migration) cover it.
/// </summary>
public class BudgetSettingsConfiguration : IEntityTypeConfiguration<BudgetSettings>
{
    public void Configure(EntityTypeBuilder<BudgetSettings> b)
    {
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.TenantId).IsUnique();
        b.Property(x => x.MonthAnchor).HasMaxLength(32).IsRequired();
        b.Property(x => x.PrimaryIncomeCurrency).HasMaxLength(3).IsRequired();
        b.Property(x => x.SecondaryIncomeCurrency).HasMaxLength(3).IsRequired();
        b.Property(x => x.PrimaryIncome4w).HasPrecision(12, 2);
        b.Property(x => x.PrimaryIncome5w).HasPrecision(12, 2);
        b.Property(x => x.SecondaryIncome4w).HasPrecision(12, 2);
        b.Property(x => x.SecondaryIncome5w).HasPrecision(12, 2);
    }
}
