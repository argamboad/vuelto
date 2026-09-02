namespace Perezosoft.Core.Abstractions;

/// <summary>
/// The tenant the current request acts within, resolved from the authenticated
/// principal (the JWT <c>tenant_id</c> claim). This is the single, request-scoped
/// tenancy entry point: it drives the global tenant query filter in the DbContext, and
/// feature slices inject it to read their tenant id.
/// <para>
/// <see cref="TenantId"/> is null when the caller is unauthenticated or has no tenant;
/// the global filter then matches no tenant-scoped rows (fail closed).
/// </para>
/// </summary>
public interface ICurrentTenant
{
    Guid? TenantId { get; }
}
