using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

/// <summary>#210: a masked pattern names exactly one card per household; patterns go with their card.</summary>
public class CardPatternConfiguration : IEntityTypeConfiguration<CardPattern>
{
    public void Configure(EntityTypeBuilder<CardPattern> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Pattern).HasMaxLength(CardPattern.PatternMaxLength).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Pattern }).IsUnique();
        b.HasOne<Card>().WithMany().HasForeignKey(x => x.CardId).OnDelete(DeleteBehavior.Cascade);
    }
}
