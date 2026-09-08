using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

/// <summary>CARDS-1: a catalog entry (unique alias per household, case-insensitivity in the handler) that is also unique by what a voucher prints — brand + last four.</summary>
public class CardConfiguration : IEntityTypeConfiguration<Card>
{
    public void Configure(EntityTypeBuilder<Card> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Brand).HasMaxLength(20).IsRequired();
        b.Property(x => x.Last4).HasMaxLength(4).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        b.HasOne<Bank>().WithMany().HasForeignKey(x => x.BankId).OnDelete(DeleteBehavior.Restrict);
    }
}
