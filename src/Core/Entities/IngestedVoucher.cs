namespace Vuelto.Core.Entities;

/// <summary>
/// The household-scoped dedup tombstone (EMAIL-4): one row per accepted voucher fingerprint, unique on
/// <c>(TenantId, Fingerprint)</c>, so re-staging is impossible at the database. <b>It outlives the draft's
/// lifecycle</b> — discarding or confirming a draft never removes this row, so a still-unread email that
/// is re-fetched on the next poll is never staged twice (mail is never marked read, ADR-V010).
/// </summary>
public class IngestedVoucher : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public required string Fingerprint { get; set; }
    public Guid PendingVoucherId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
