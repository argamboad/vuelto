using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Perezosoft.Core.Entities;

namespace Perezosoft.Infrastructure.Persistence.Configurations;

public class UsageCounterConfiguration : IEntityTypeConfiguration<UsageCounter>
{
    public void Configure(EntityTypeBuilder<UsageCounter> u)
    {
        u.HasKey(x => x.Id);
        u.Property(x => x.Key).HasMaxLength(64).IsRequired();
        u.Property(x => x.Period).HasMaxLength(7).IsRequired(); // yyyy-MM
        // One counter row per (tenant, key, period) — the upsert target for TryConsume.
        u.HasIndex(x => new { x.TenantId, x.Key, x.Period }).IsUnique();
    }
}
