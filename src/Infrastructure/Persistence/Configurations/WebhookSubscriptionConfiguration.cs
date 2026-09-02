using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

public class WebhookSubscriptionConfiguration : IEntityTypeConfiguration<WebhookSubscription>
{
    public void Configure(EntityTypeBuilder<WebhookSubscription> w)
    {
        w.HasKey(x => x.Id);
        w.Property(x => x.Url).HasMaxLength(2048).IsRequired();
        w.Property(x => x.EventTypes).HasMaxLength(1024).IsRequired();
        w.Property(x => x.EncryptedSecret).IsRequired();
        w.HasIndex(x => x.TenantId);
    }
}
