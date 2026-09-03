using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

/// <summary>EMAIL-4: the review draft. Household-scoped (RLS); the bank reference is Restrict (banks soft-delete); the connection is a soft cross-axis reference.</summary>
public class PendingVoucherConfiguration : IEntityTypeConfiguration<PendingVoucher>
{
    public void Configure(EntityTypeBuilder<PendingVoucher> p)
    {
        p.HasKey(x => x.Id);
        p.Property(x => x.ProviderMessageId).HasMaxLength(512).IsRequired();
        p.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
        p.Property(x => x.ParsedBank).HasMaxLength(20).IsRequired();
        p.Property(x => x.Merchant).HasMaxLength(200);
        p.Property(x => x.Amount).HasPrecision(12, 2);
        p.Property(x => x.Currency).HasMaxLength(3);
        p.Property(x => x.CardNumber).HasMaxLength(40);
        p.Property(x => x.Authorization).HasMaxLength(64);
        p.Property(x => x.Reference).HasMaxLength(64);
        p.Property(x => x.TransactionType).HasMaxLength(40);
        p.Property(x => x.MissingFields).IsRequired();
        p.Property(x => x.SuggestedClass).HasMaxLength(32);
        p.Property(x => x.Status).HasMaxLength(20).IsRequired();
        p.HasIndex(x => new { x.TenantId, x.Status });
        p.HasOne<Bank>().WithMany().HasForeignKey(x => x.BankId).OnDelete(DeleteBehavior.Restrict);
        p.HasOne<Category>().WithMany().HasForeignKey(x => x.SuggestedCategoryId).OnDelete(DeleteBehavior.SetNull);
    }
}
