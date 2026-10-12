namespace Vuelto.Core.Abstractions;

/// <summary>
/// Enters a tenant context for system/integration operations that have no JWT but a <b>trusted</b>
/// tenant id — e.g. a Stripe-signature-verified webhook, or (future) admin impersonation. While the
/// returned scope is active, <see cref="ICurrentTenant.TenantId"/> resolves to the entered tenant, so
/// the write-stamping interceptor and the global read filter scope to it. Such operations therefore
/// get the <b>same structural tenant isolation an authenticated request gets</b>, instead of bypassing
/// it with the cross-tenant escape hatch (<c>QueryAllTenants()</c> / <c>IgnoreQueryFilters()</c>) — see
/// ADR-003.
/// <para>
/// This primitive only <em>scopes</em>; it does not <em>authorize</em>. The caller MUST have already
/// authenticated the tenant id by other means (a verified webhook signature, an admin authz check).
/// </para>
/// </summary>
public interface ITenantContext
{
    /// <summary>
    /// Makes <paramref name="tenantId"/> the current tenant until the returned scope is disposed.
    /// Nesting is supported (the previous tenant is restored on dispose). <paramref name="tenantId"/>
    /// must not be <see cref="Guid.Empty"/> — that is the system/no-tenant context, not a tenant.
    /// </summary>
    IDisposable EnterTenant(Guid tenantId);
}
