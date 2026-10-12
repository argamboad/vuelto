using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

/// <summary>
/// Category names are unique per household. The DB index is exact-case (it also serialises the
/// first-read seeding race); the handler enforces the case-insensitive rule (ADR-V008).
/// </summary>
public class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
    }
}
