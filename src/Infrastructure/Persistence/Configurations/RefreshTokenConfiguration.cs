using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

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
        // RotatedAt / ReplacedByTokenId (rotation link, reuse grace window — ADR-002 addendum 2026-09-18) are
        // plain nullable columns by convention. ReplacedByTokenId is deliberately NOT a foreign key: the hourly
        // expired-token cleanup deletes rows set-based in any order, and the successor is only ever read by id.
    }
}
