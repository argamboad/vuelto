using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> o)
    {
        o.HasKey(x => x.Id);
        o.Property(x => x.Type).HasMaxLength(128).IsRequired();
        o.Property(x => x.Status).HasMaxLength(16).IsRequired();
        o.Property(x => x.Payload).IsRequired();
        o.Property(x => x.LastError).HasMaxLength(1000);
        // Drives the dispatcher claim query: pending + due, oldest first.
        o.HasIndex(x => new { x.Status, x.NextAttemptAt });
    }
}
