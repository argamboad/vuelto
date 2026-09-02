using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

public class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> i)
    {
        i.HasKey(x => x.Id);
        i.Property(x => x.Source).HasMaxLength(64).IsRequired();
        i.Property(x => x.IdempotencyKey).HasMaxLength(256).IsRequired();
        // Dedup arbiter: one row per (source, key). The unique index is the ON CONFLICT target
        // that makes EfInbox.TryClaimAsync race-free.
        i.HasIndex(x => new { x.Source, x.IdempotencyKey }).IsUnique();
    }
}
