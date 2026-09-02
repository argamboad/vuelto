using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Perezosoft.Core.Entities;

namespace Perezosoft.Infrastructure.Persistence;

/// <summary>
/// Enforces that <see cref="AuditEvent"/> is <b>append-only</b> (OBS-4, ADR-008): a tracked audit row in
/// <c>Modified</c> or <c>Deleted</c> state at <c>SaveChanges</c> throws, so application code can insert
/// audit events but never alter or remove them. Set-based deletes (<c>ExecuteDelete</c>) don't pass
/// through the change tracker, so tenant dissolve can still purge a tenant's trail. Stateless; one shared
/// instance, like <see cref="TenantStampingInterceptor"/>.
/// </summary>
public sealed class AuditAppendOnlyInterceptor : SaveChangesInterceptor
{
    public static readonly AuditAppendOnlyInterceptor Instance = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Guard(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Guard(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Guard(DbContext? db)
    {
        if (db is null) return;

        foreach (var entry in db.ChangeTracker.Entries<AuditEvent>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException(
                    "AuditEvent is append-only: updates and deletes are not permitted from application code.");
        }
    }
}
