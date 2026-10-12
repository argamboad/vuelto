using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

public class MfaRecoveryCodeConfiguration : IEntityTypeConfiguration<MfaRecoveryCode>
{
    public void Configure(EntityTypeBuilder<MfaRecoveryCode> c)
    {
        c.HasKey(x => x.Id);
        c.Property(x => x.CodeHash).HasMaxLength(256).IsRequired();
        c.HasIndex(x => x.UserId);
    }
}
