using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> u)
    {
        u.HasKey(x => x.Id);
        u.Property(x => x.Email).HasMaxLength(256).IsRequired();
        u.Property(x => x.DisplayName).HasMaxLength(256);
        u.Property(x => x.Locale).HasMaxLength(10);
        u.Property(x => x.Theme).HasMaxLength(10);
        u.HasIndex(x => x.Email).IsUnique();
        u.HasMany(x => x.Logins)
         .WithOne(x => x.User)
         .HasForeignKey(x => x.UserId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}
