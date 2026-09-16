using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

/// <summary>
/// INCOME-1 (ADR-V023): the household's income lines. Unique name per household (case-insensitivity in the handler),
/// NUMERIC(12,2) amounts (ADR-V004), an index for the ordered list. <c>MemberUserId</c> is a plain id, not a foreign
/// key: the line is household data, the user is identity data, and account erasure clears the reference.
/// </summary>
public class IncomeLineConfiguration : IEntityTypeConfiguration<IncomeLine>
{
    public void Configure(EntityTypeBuilder<IncomeLine> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
        b.Property(x => x.PayPeriod).HasMaxLength(20).IsRequired();
        b.Property(x => x.Amount).HasPrecision(12, 2);
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.SortOrder });
    }
}
