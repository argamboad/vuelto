namespace Vuelto.Core.Abstractions;

/// <summary>
/// A feature's contribution to tenant-data presence and teardown. Each vertical slice that
/// owns tenant-scoped tables registers one of these in DI; the platform queries all
/// contributors instead of a hard-coded method — so adding a feature never means editing a
/// central "has data" / "wipe data" method.
/// <para>
/// "Has data" guards a solo owner from silently abandoning data by joining another tenant;
/// "Wipe" runs inside the dissolve transaction. The platform handles the core teardown
/// (memberships, invitations, the tenant row) itself — contributors handle only their own
/// domain tables.
/// </para>
/// </summary>
public interface ITenantDataContributor
{
    /// <summary>True if this contributor holds any data for the tenant.</summary>
    Task<bool> HasDataAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Removes this contributor's data for the tenant. Called within the dissolve
    /// transaction, so it shares the caller's unit of work — do not commit here.</summary>
    Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The section name this contributor's data appears under in a tenant export (GDPR-1, ADR-011),
    /// e.g. <c>"notes"</c>. Stable and unique across contributors.
    /// </summary>
    string ExportKey { get; }

    /// <summary>
    /// A JSON-serializable snapshot of this contributor's data for the tenant, for a data export
    /// (GDPR-1). Return <c>null</c> when there is nothing to include. <b>Identifiers and content only —
    /// never secrets</b> (no token/password hashes, no card data), same rule as audit metadata.
    /// </summary>
    Task<object?> ExportAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
