using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

public class TenantInvitationConfiguration : IEntityTypeConfiguration<TenantInvitation>
{
    public void Configure(EntityTypeBuilder<TenantInvitation> i)
    {
        i.HasKey(x => x.Id);
        i.Property(x => x.InvitedEmail).HasMaxLength(256).IsRequired();
        i.Property(x => x.TokenHash).HasMaxLength(256).IsRequired();
        i.Property(x => x.Status).HasMaxLength(32).IsRequired();
        i.HasOne<Tenant>()
         .WithMany()
         .HasForeignKey(x => x.TenantId)
         .OnDelete(DeleteBehavior.Cascade);
        // Unique: a hash identifies exactly one invitation (single-row credential lookup).
        i.HasIndex(x => x.TokenHash).IsUnique();
        i.HasIndex(x => new { x.TenantId, x.Status });
        // Ignore computed properties — derived, never stored.
        i.Ignore(x => x.IsExpired);
        i.Ignore(x => x.IsValid);
    }
}
