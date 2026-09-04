using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

/// <summary>
/// EMAIL-5: a household's merchant → category rules. One rule per merchant text per household regardless
/// of casing — the unique index is on the stored lower-cased <c>PatternKey</c>, so a concurrent create that
/// slips past the pre-check fails at the database (→ 409). Category is Restrict: categories soft-delete.
/// </summary>
public class MerchantCategoryMappingConfiguration : IEntityTypeConfiguration<MerchantCategoryMapping>
{
    public void Configure(EntityTypeBuilder<MerchantCategoryMapping> m)
    {
        m.HasKey(x => x.Id);
        m.Property(x => x.MerchantPattern).HasMaxLength(200).IsRequired();
        m.Property(x => x.PatternKey).HasMaxLength(200).IsRequired();
        m.Property(x => x.SuggestedClass).HasMaxLength(32);
        m.HasIndex(x => new { x.TenantId, x.PatternKey }).IsUnique();
        m.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
    }
}
