using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

public class ApiKeyConfiguration : IEntityTypeConfiguration<ApiKey>
{
    public void Configure(EntityTypeBuilder<ApiKey> k)
    {
        k.HasKey(x => x.Id);
        k.Property(x => x.Name).HasMaxLength(128).IsRequired();
        k.Property(x => x.KeyHash).HasMaxLength(128).IsRequired();
        k.Property(x => x.Prefix).HasMaxLength(32).IsRequired();
        k.Property(x => x.Scopes).HasMaxLength(256).IsRequired();
        // Auth looks a presented key up by hash (across tenants, pre-scope) — unique + indexed.
        k.HasIndex(x => x.KeyHash).IsUnique();
        k.HasIndex(x => x.TenantId);
    }
}
