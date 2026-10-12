using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

/// <summary>
/// INCOME-1 (ADR-V023): a month's income rows — deleted with the month (CASCADE: months exist only through
/// transactions and vanish with the last one, ADR-V005), and kept when their line goes (SET NULL: the row already
/// carries its label, member and currency). NUMERIC(12,2) (ADR-V004).
/// </summary>
public class MonthIncomeConfiguration : IEntityTypeConfiguration<MonthIncome>
{
    public void Configure(EntityTypeBuilder<MonthIncome> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Label).HasMaxLength(100).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.Amount).HasPrecision(12, 2);
        b.Property(x => x.PlannedAmount).HasPrecision(12, 2);
        b.HasOne<Month>().WithMany().HasForeignKey(x => x.MonthId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<IncomeLine>().WithMany().HasForeignKey(x => x.IncomeLineId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => new { x.TenantId, x.MonthId, x.SortOrder });
    }
}
