namespace Vuelto.Core.Entities;

/// <summary>A materialized week of a <see cref="Month"/> (inclusive dates), stored at creation and never recomputed (ADR-V005).</summary>
public class Week : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid MonthId { get; set; }
    public int WeekNumber { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
}
