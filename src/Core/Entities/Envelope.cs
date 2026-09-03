using Vuelto.Core.Budget;

namespace Vuelto.Core.Entities;

/// <summary>
/// A savings bucket for a large annual expense (marchamo, school, a trip): an annual target in one or
/// both currencies and a reminder cadence (ADR-V007). Contributions are <c>envelope_contribution</c>
/// transactions (P5) — the entity holds no running balance. Soft-deleted like every catalog (ADR-V008),
/// so it shares <see cref="ICatalogEntry"/> and the catalog rules: unique name per household,
/// case-insensitively; inactive entries keep labelling history. Never seeded — targets are personal.
/// </summary>
public class Envelope : ICatalogEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public required string Name { get; set; }
    public decimal AnnualTargetCrc { get; set; }
    public decimal AnnualTargetUsd { get; set; }

    /// <summary>One of <see cref="EnvelopeReminderCadences"/>.</summary>
    public string ReminderCadence { get; set; } = EnvelopeReminderCadences.Monthly;

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
