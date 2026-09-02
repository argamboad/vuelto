using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

public class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> d)
    {
        d.HasKey(x => x.Id);
        d.Property(x => x.EventType).HasMaxLength(128).IsRequired();
        d.Property(x => x.EventId).HasMaxLength(64).IsRequired();
        d.Property(x => x.Error).HasMaxLength(1000);
        // Read pattern: a subscription's attempts newest-first, tenant-filtered.
        d.HasIndex(x => new { x.TenantId, x.SubscriptionId, x.CreatedAt });
    }
}
