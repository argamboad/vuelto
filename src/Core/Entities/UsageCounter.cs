namespace Vuelto.Core.Entities;

/// <summary>
/// A per-tenant, per-period metered-usage counter (BILLING-5). One row per
/// (tenant, usage key, period) — the period is a calendar month (<c>yyyy-MM</c>), so a new month starts a
/// fresh counter with no reset job. <c>IQuotaService.TryConsumeAsync</c> increments it and denies once the
/// plan's monthly limit is reached. Tenant-scoped (ADR-003).
/// </summary>
public class UsageCounter : ITenantScoped
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }

    /// <summary>The metered action (a <c>UsageKeys</c> value), e.g. "export".</summary>
    public required string Key { get; set; }

    /// <summary>The billing period this count belongs to — a calendar month, <c>yyyy-MM</c> (UTC).</summary>
    public required string Period { get; set; }

    /// <summary>Units consumed in this (tenant, key, period) so far.</summary>
    public int Count { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
