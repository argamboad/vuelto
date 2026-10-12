using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Vuelto.Core.Entities;

namespace Vuelto.Infrastructure.Persistence.Configurations;

/// <summary>Weeks live and die with their month (cascade); one row per week number.</summary>
public class WeekConfiguration : IEntityTypeConfiguration<Week>
{
    public void Configure(EntityTypeBuilder<Week> b)
    {
        b.HasKey(x => x.Id);
        b.HasOne<Month>().WithMany().HasForeignKey(x => x.MonthId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => new { x.MonthId, x.WeekNumber }).IsUnique();
    }
}
