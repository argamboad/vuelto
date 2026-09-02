using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Perezosoft.Core.Repositories;
using Perezosoft.Infrastructure.Persistence;

namespace Perezosoft.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IUnitOfWork"/>. Wraps a real
/// <see cref="IDbContextTransaction"/> on a relational provider; on a
/// non-relational provider (EF InMemory in tests, which doesn't support
/// transactions) it returns a no-op scope so service code is provider-agnostic.
/// All repositories share this scoped DbContext, so their SaveChanges calls
/// enlist in the same transaction.
///
/// Nested calls: when a relational transaction is already active,
/// <see cref="BeginTransactionAsync"/> creates a savepoint instead of starting a
/// new transaction. The inner scope commits by releasing the savepoint; the outer
/// scope commits the transaction. An un-committed inner scope rolls back to the
/// savepoint without aborting the outer transaction.
/// </summary>
public class EfUnitOfWork(AppDbContext db) : IUnitOfWork
{
    public async Task<ITransactionScope> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsRelational())
            return NoOpScope.Instance;

        if (db.Database.CurrentTransaction is { } existing)
        {
            // Nested call inside an already-open transaction: use a savepoint so
            // the inner unit can roll back independently without aborting the outer
            // transaction (Postgres aborts the whole tx on any error otherwise).
            // Opaque unique savepoint name (not a DB key) — random Guid, not UUIDv7.
            var name = $"sp_{Guid.NewGuid():N}";
            await existing.CreateSavepointAsync(name, cancellationToken);
            return new SavepointScope(existing, db, name);
        }

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        return new EfTransactionScope(transaction);
    }

    private sealed class EfTransactionScope(IDbContextTransaction transaction) : ITransactionScope
    {
        public Task CommitAsync(CancellationToken cancellationToken = default) => transaction.CommitAsync(cancellationToken);
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }

    private sealed class SavepointScope(IDbContextTransaction transaction, AppDbContext db, string name) : ITransactionScope
    {
        private bool _committed;

        public async Task CommitAsync(CancellationToken cancellationToken = default)
        {
            await transaction.ReleaseSavepointAsync(name, cancellationToken);
            _committed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                await transaction.RollbackToSavepointAsync(name);
                // Clear the EF change tracker so entities added during the
                // rolled-back scope are not re-sent on the next SaveChangesAsync.
                db.ChangeTracker.Clear();
            }
        }
    }

    private sealed class NoOpScope : ITransactionScope
    {
        public static readonly NoOpScope Instance = new();
        public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
