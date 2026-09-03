using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

/// <summary>
/// Transactions cascade with their month; the catalog parents (bank, category, envelope) are
/// <b>never</b> cascaded — they are soft-deleted and must keep naming history (DATA_MODEL). Amounts
/// are NUMERIC(12,2), the frozen rate NUMERIC(10,4) (ADR-V004/V006). Indexes serve the month list,
/// date-range reports and the "most recent rate" tier of the exchange-rate chain.
/// </summary>
public class TransactionConfiguration : IEntityTypeConfiguration<Transaction>
{
    public void Configure(EntityTypeBuilder<Transaction> b)
    {
        b.HasKey(x => x.Id);
        b.HasOne<Month>().WithMany().HasForeignKey(x => x.MonthId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Bank>().WithMany().HasForeignKey(x => x.BankId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Envelope>().WithMany().HasForeignKey(x => x.EnvelopeId).OnDelete(DeleteBehavior.Restrict);

        b.Property(x => x.Payee).HasMaxLength(200).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.PaymentMethod).HasMaxLength(32).IsRequired();
        b.Property(x => x.TransactionType).HasMaxLength(32).IsRequired();
        b.Property(x => x.Source).HasMaxLength(32).IsRequired();
        b.Property(x => x.OriginalAmount).HasPrecision(12, 2);
        b.Property(x => x.AmountCrc).HasPrecision(12, 2);
        b.Property(x => x.AmountUsd).HasPrecision(12, 2);
        b.Property(x => x.ExchangeRateUsed).HasPrecision(10, 4);

        b.HasIndex(x => new { x.TenantId, x.MonthId });
        b.HasIndex(x => new { x.TenantId, x.TransactionDate });
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
    }
}
