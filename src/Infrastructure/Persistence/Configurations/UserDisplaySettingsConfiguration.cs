using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

/// <summary>DISPLAY-1: user-keyed (no TenantId, no RLS policy — ADR-V002/V020); one row per user, gone with the user.</summary>
public class UserDisplaySettingsConfiguration : IEntityTypeConfiguration<UserDisplaySettings>
{
    public void Configure(EntityTypeBuilder<UserDisplaySettings> c)
    {
        c.HasKey(x => x.Id);
        c.Property(x => x.DisplayCurrency).HasMaxLength(5).IsRequired();
        c.HasIndex(x => x.UserId).IsUnique();
        c.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
