using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

/// <summary>
/// Catalog rules (unique name per household, case-insensitivity in the handler) plus the app-wide
/// <c>NUMERIC(12,2)</c> for the two annual targets (ADR-V004). ITenantScoped via ICatalogEntry, so
/// the global filter + the RLS policy shipped in the same migration cover it.
/// </summary>
public class EnvelopeConfiguration : IEntityTypeConfiguration<Envelope>
{
    public void Configure(EntityTypeBuilder<Envelope> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.ReminderCadence).HasMaxLength(32).IsRequired();
        b.Property(x => x.AnnualTargetCrc).HasPrecision(12, 2);
        b.Property(x => x.AnnualTargetUsd).HasPrecision(12, 2);
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
    }
}
