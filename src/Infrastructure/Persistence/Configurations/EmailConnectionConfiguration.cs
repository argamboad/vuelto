using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

/// <summary>EMAIL-2: user-keyed (no TenantId, no RLS policy — ADR-V002); one inbox per provider per user; text[] columns for folders/filters.</summary>
public class EmailConnectionConfiguration : IEntityTypeConfiguration<EmailConnection>
{
    public void Configure(EntityTypeBuilder<EmailConnection> c)
    {
        c.HasKey(x => x.Id);
        c.Property(x => x.Provider).HasMaxLength(20).IsRequired();
        c.Property(x => x.AccountEmail).HasMaxLength(320);
        c.Property(x => x.AccessToken).IsRequired();
        c.Property(x => x.RefreshToken).IsRequired();
        c.Property(x => x.Status).HasMaxLength(30).IsRequired();
        c.Property(x => x.Folders).IsRequired();
        c.Property(x => x.SenderFilters).IsRequired();
        c.Property(x => x.SubjectFilters).IsRequired();
        c.HasIndex(x => new { x.UserId, x.Provider }).IsUnique();
        c.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
