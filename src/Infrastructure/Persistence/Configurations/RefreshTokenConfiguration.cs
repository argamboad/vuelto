using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Perezosoft.Core.Entities;

namespace Perezosoft.Infrastructure.Persistence.Configurations;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> r)
    {
        r.HasKey(x => x.Id);
        r.Property(x => x.TokenHash).HasMaxLength(256).IsRequired();
        r.Property(x => x.IssuedFromIp).HasMaxLength(64).IsRequired();
        r.Property(x => x.Provider).HasMaxLength(64).IsRequired();
        // Unique: a hash identifies exactly one refresh token (single-row credential lookup).
        r.HasIndex(x => x.TokenHash).IsUnique();
        r.HasIndex(x => x.UserId);
    }
}
