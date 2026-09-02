namespace Perezosoft.Core.Entities;

/// <summary>
/// SAMPLE feature entity — a tenant-scoped note. It exists only to demonstrate the
/// vertical-slice convention (see <c>src/Api/Features/Notes</c> and
/// <c>docs/WAYS_OF_WORKING.md</c>).
/// <para>
/// 🗑️ DELETE-ME: when you build your first real feature, remove this entity, the
/// <c>Notes</c> DbSet + config in <c>AppDbContext</c>, the <c>AddNotesSample</c> migration,
/// the <c>Features/Notes</c> folder, and its registration in <c>Program.cs</c>.
/// </para>
/// </summary>
public class Note : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public required string Title { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
