namespace Perezosoft.Core.Repositories;

/// <summary>
/// A transactional boundary spanning several repository writes.
/// On a relational provider this is a real database transaction; on a
/// non-relational provider (EF InMemory in tests) it degrades to a no-op so the
/// exact same service code path runs in both. Begin → do work → CommitAsync;
/// disposing without committing rolls back (relational only).
/// <para>
/// <b>Commit model (read this before adding a repository).</b> Every repository — the dedicated
/// ones and the generic <c>IRepository&lt;T&gt;</c> — shares the <em>same</em> request-scoped
/// <c>DbContext</c>. There is no single "commit authority": any <c>SaveChangesAsync</c> call (on
/// any repository, or <c>IRepository&lt;T&gt;.SaveChangesAsync</c>) FLUSHES <em>all</em> pending
/// tracked changes on that shared context, not just the calling repository's. That is intentional
/// and safe because of how the two layers compose:
/// <list type="bullet">
///   <item><b>Atomicity</b> is this interface's job: a service that must group several writes wraps
///   them in <c>BeginTransactionAsync</c> → … → <c>CommitAsync</c>. Inside that scope every
///   intermediate <c>SaveChangesAsync</c> enlists in the one open transaction/savepoint, so a
///   mid-sequence failure (or a scope disposed without commit) rolls the whole group back.</item>
///   <item><b>A single self-contained write</b> (one repository method, no cross-entity invariant)
///   may just call its <c>SaveChangesAsync</c> directly and rely on EF's implicit per-call
///   transaction — no explicit scope needed.</item>
/// </list>
/// Set-based writes (<c>ExecuteUpdateAsync</c>/<c>ExecuteDeleteAsync</c>) run their own statement
/// immediately; inside a transaction scope they enlist in it, otherwise they auto-commit.
/// </para>
/// </summary>
public interface IUnitOfWork
{
    Task<ITransactionScope> BeginTransactionAsync(CancellationToken cancellationToken = default);
}

public interface ITransactionScope : IAsyncDisposable
{
    Task CommitAsync(CancellationToken cancellationToken = default);
}
