using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

public class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> a)
    {
        a.HasKey(x => x.Id);
        a.Property(x => x.Action).HasMaxLength(128).IsRequired();
        a.Property(x => x.EntityType).HasMaxLength(128);
        a.Property(x => x.EntityId).HasMaxLength(256);
        a.Property(x => x.Metadata).HasColumnType("jsonb");
        // Read pattern: a tenant's trail, newest first.
        a.HasIndex(x => new { x.TenantId, x.CreatedAt });
    }
}
