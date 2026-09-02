using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Perezosoft.Core.Entities;

namespace Perezosoft.Infrastructure.Persistence.Configurations;

public class LoginTokenConfiguration : IEntityTypeConfiguration<LoginToken>
{
    public void Configure(EntityTypeBuilder<LoginToken> t)
    {
        t.HasKey(x => x.Id);
        t.Property(x => x.Email).HasMaxLength(256).IsRequired();
        t.Property(x => x.CodeHash).HasMaxLength(256).IsRequired();
        t.Property(x => x.Purpose).HasMaxLength(32).IsRequired();
        t.HasIndex(x => new { x.Email, x.Purpose });
        // Derived, never stored.
        t.Ignore(x => x.IsConsumed);
        t.Ignore(x => x.IsExpired);
        t.Ignore(x => x.IsValid);
    }
}
