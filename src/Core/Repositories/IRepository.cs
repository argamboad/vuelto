namespace Vuelto.Core.Repositories;

/// <summary>
/// A thin generic repository for FEATURE/domain entities, so a vertical slice can read and
/// write its own data without authoring a bespoke repository pair. Reads via
/// <see cref="Query"/> are automatically tenant-scoped for <c>ITenantScoped</c> entities
/// (the AppDbContext global query filter), so a slice can't forget to scope.
/// <para>
/// Platform/auth entities (User, Tenant, tokens, …) keep their dedicated repositories;
/// use this only for app/domain tables.
/// </para>
/// </summary>
public interface IRepository<TEntity> where TEntity : class
{
    /// <summary>
    /// Queryable over the set — tenant-filtered for <c>ITenantScoped</c> entities. Compose
    /// typed LINQ in the slice, e.g. <c>Query().Where(...).ToListAsync(ct)</c>. This is the
    /// normal feature read path; it cannot leak across tenants.
    /// </summary>
    IQueryable<TEntity> Query();

    /// <summary>
    /// Cross-tenant queryable: the global tenant filter is OFF, so this sees EVERY tenant's
    /// rows. This is the audited, deliberately-named escape hatch for the rare genuinely
    /// cross-tenant path (e.g. a dissolve <c>ITenantDataContributor</c> wiping a tenant other
    /// than the current one). Feature slices use <see cref="Query"/>; reach for this only when
    /// crossing tenants is the explicit intent, and always re-constrain with a
    /// <c>Where(x =&gt; x.TenantId == ...)</c>. Greppable on purpose.
    /// </summary>
    IQueryable<TEntity> QueryAllTenants();

    Task AddAsync(TEntity entity, CancellationToken cancellationToken = default);
    void Update(TEntity entity);
    void Remove(TEntity entity);

    /// <summary>
    /// Flushes pending changes on the shared request-scoped context and returns the affected row
    /// count. This persists ALL tracked changes, not only this entity's — see
    /// <see cref="IUnitOfWork"/> for the commit/atomicity model.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
