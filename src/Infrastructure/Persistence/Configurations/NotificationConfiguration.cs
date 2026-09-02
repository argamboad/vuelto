using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> n)
    {
        n.HasKey(x => x.Id);
        n.Property(x => x.Kind).HasMaxLength(64).IsRequired();
        n.Property(x => x.Title).HasMaxLength(256).IsRequired();
        n.Property(x => x.Metadata).HasColumnType("jsonb");
        // Per-user feed: list newest-first + unread counts.
        n.HasIndex(x => new { x.UserId, x.CreatedAt });
    }
}
