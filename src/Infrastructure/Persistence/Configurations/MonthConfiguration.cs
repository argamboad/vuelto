using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

/// <summary>
/// One budget month per (household, year, month) — the unique index is what makes the concurrent
/// first-transaction race safe (the loser retries and finds the winner's month). The
/// (TenantId, Week1StartDate) index serves the anchor-window lookup. Money is NUMERIC(12,2) (ADR-V004).
/// </summary>
public class MonthConfiguration : IEntityTypeConfiguration<Month>
{
    public void Configure(EntityTypeBuilder<Month> b)
    {
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.TenantId, x.Year, x.MonthNumber }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.Week1StartDate });
        b.Property(x => x.PrimaryIncomeCurrency).HasMaxLength(3).IsRequired();
        b.Property(x => x.SecondaryIncomeCurrency).HasMaxLength(3).IsRequired();
        b.Property(x => x.PrimaryIncomeAmount).HasPrecision(12, 2);
        b.Property(x => x.SecondaryIncomeAmount).HasPrecision(12, 2);
    }
}
