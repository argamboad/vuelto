using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Perezosoft.Core.Entities;

namespace Perezosoft.Infrastructure.Persistence.Configurations;

// 🗑️ DELETE-ME: sample feature (remove with the Features/Notes slice). Implements
// ITenantScoped, so the global query filter in AppDbContext covers it automatically.
public class NoteConfiguration : IEntityTypeConfiguration<Note>
{
    public void Configure(EntityTypeBuilder<Note> n)
    {
        n.HasKey(x => x.Id);
        n.Property(x => x.Title).HasMaxLength(200).IsRequired();
        n.HasIndex(x => x.TenantId);
    }
}
