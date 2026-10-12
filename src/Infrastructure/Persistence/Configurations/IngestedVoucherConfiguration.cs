using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

/// <summary>EMAIL-4: the dedup tombstone — unique per household fingerprint (the database is the last line of defence against a re-stage race).</summary>
public class IngestedVoucherConfiguration : IEntityTypeConfiguration<IngestedVoucher>
{
    public void Configure(EntityTypeBuilder<IngestedVoucher> i)
    {
        i.HasKey(x => x.Id);
        i.Property(x => x.Fingerprint).HasMaxLength(64).IsRequired();
        i.HasIndex(x => new { x.TenantId, x.Fingerprint }).IsUnique();
    }
}
