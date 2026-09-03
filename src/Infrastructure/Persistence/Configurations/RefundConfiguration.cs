using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

/// <summary>
/// A refund lives and dies with its source transaction (cascade, one per transaction) and its month;
/// the realized inflow link is SET NULL so deleting the inflow can never strand a refund on a dangling
/// id. Money is NUMERIC(12,2), the percentage NUMERIC(5,2) (ADR-V004).
/// </summary>
public class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> b)
    {
        b.HasKey(x => x.Id);
        b.HasOne<Month>().WithMany().HasForeignKey(x => x.MonthId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Transaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Transaction>().WithMany().HasForeignKey(x => x.InflowTransactionId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => x.TransactionId).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.MonthId });

        b.Property(x => x.Payee).HasMaxLength(200).IsRequired();
        b.Property(x => x.Status).HasMaxLength(16).IsRequired();
        b.Property(x => x.Percentage).HasPrecision(5, 2);
        b.Property(x => x.AmountCrc).HasPrecision(12, 2);
        b.Property(x => x.AmountUsd).HasPrecision(12, 2);
    }
}
