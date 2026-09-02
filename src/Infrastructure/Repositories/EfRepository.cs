using Microsoft.EntityFrameworkCore;
using Perezosoft.Core.Repositories;
using Perezosoft.Infrastructure.Persistence;

namespace Perezosoft.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of the generic feature repository. Registered open-generically
/// (<c>IRepository&lt;&gt; → EfRepository&lt;&gt;</c>) so any entity type resolves without
/// per-entity wiring.
/// </summary>
public class EfRepository<TEntity>(AppDbContext db) : IRepository<TEntity> where TEntity : class
{
    public IQueryable<TEntity> Query() => db.Set<TEntity>();

    // TagWith declares the sanctioned cross-tenant read to the RLS backstop (ADR-020) — without it
    // the DB policy would still scope the query to the caller's current tenant.
    public IQueryable<TEntity> QueryAllTenants() =>
        db.Set<TEntity>().IgnoreQueryFilters().TagWith(RlsTags.CrossTenant);

    public async Task AddAsync(TEntity entity, CancellationToken cancellationToken = default) =>
        await db.Set<TEntity>().AddAsync(entity, cancellationToken);

    public void Update(TEntity entity) => db.Set<TEntity>().Update(entity);

    public void Remove(TEntity entity) => db.Set<TEntity>().Remove(entity);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
