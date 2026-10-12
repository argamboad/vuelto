using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

public class TenantMembershipConfiguration : IEntityTypeConfiguration<TenantMembership>
{
    public void Configure(EntityTypeBuilder<TenantMembership> m)
    {
        m.HasKey(x => x.Id);
        m.Property(x => x.Role).HasMaxLength(32).IsRequired();
        // One tenant per user at a time.
        m.HasIndex(x => x.UserId).IsUnique();
        m.HasIndex(x => x.TenantId);
        m.HasOne<Tenant>()
         .WithMany()
         .HasForeignKey(x => x.TenantId)
         .OnDelete(DeleteBehavior.Cascade);
        m.HasOne<User>()
         .WithMany()
         .HasForeignKey(x => x.UserId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}
