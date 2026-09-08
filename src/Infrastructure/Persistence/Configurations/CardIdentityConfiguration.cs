using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

/// <summary>CARDS-1: the (brand, last four) pairs a card is known by; a pair names exactly one card per household; identities go with their card.</summary>
public class CardIdentityConfiguration : IEntityTypeConfiguration<CardIdentity>
{
    public void Configure(EntityTypeBuilder<CardIdentity> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Brand).HasMaxLength(20).IsRequired();
        b.Property(x => x.Last4).HasMaxLength(4).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Brand, x.Last4 }).IsUnique();
        b.HasOne<Card>().WithMany().HasForeignKey(x => x.CardId).OnDelete(DeleteBehavior.Cascade);
    }
}
