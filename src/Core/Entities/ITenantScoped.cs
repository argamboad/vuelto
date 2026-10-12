namespace Vuelto.Core.Entities;

/// <summary>
/// Marks an entity as belonging to a single tenant. Every entity implementing this is
/// automatically filtered to the current tenant by a global EF query filter (see
/// <c>AppDbContext.OnModelCreating</c>), so feature/domain queries are tenant-scoped by
/// default — you cannot forget to scope a read.
/// <para>
/// Platform entities that are intentionally cross-tenant or used before a tenant is
/// resolved (<c>User</c>, <c>TenantMembership</c>, auth tokens) deliberately do NOT
/// implement this. The rare cross-tenant/pre-auth lookup opts out with
/// <c>IgnoreQueryFilters()</c>.
/// </para>
/// </summary>
public interface ITenantScoped
{
    Guid TenantId { get; }
}
