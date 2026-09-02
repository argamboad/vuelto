using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Perezosoft.Core.Entities;

namespace Perezosoft.Infrastructure.Persistence.Configurations;

public class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> p)
    {
        p.HasKey(x => x.Id);
        p.HasIndex(x => x.UserId).IsUnique(); // one preferences row per user
    }
}
