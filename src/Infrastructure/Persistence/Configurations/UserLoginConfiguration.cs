using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

public class UserLoginConfiguration : IEntityTypeConfiguration<UserLogin>
{
    public void Configure(EntityTypeBuilder<UserLogin> l)
    {
        l.HasKey(x => x.Id);
        l.Property(x => x.Provider).HasMaxLength(64).IsRequired();
        l.Property(x => x.ProviderUserId).HasMaxLength(256).IsRequired();
        l.HasIndex(x => new { x.Provider, x.ProviderUserId }).IsUnique();
        l.HasIndex(x => x.UserId);
    }
}
