using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

public class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> s)
    {
        s.HasKey(x => x.Id);
        s.Property(x => x.PlanKey).HasMaxLength(64).IsRequired();
        s.Property(x => x.Status).HasMaxLength(32).IsRequired();
        s.Property(x => x.StripeCustomerId).HasMaxLength(256);
        s.Property(x => x.StripeSubscriptionId).HasMaxLength(256);
        // At most one subscription per tenant.
        s.HasIndex(x => x.TenantId).IsUnique();
    }
}
