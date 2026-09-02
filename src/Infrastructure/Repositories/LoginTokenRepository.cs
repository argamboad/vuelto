using Microsoft.EntityFrameworkCore;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;
using Perezosoft.Infrastructure.Persistence;

namespace Perezosoft.Infrastructure.Repositories;

public class LoginTokenRepository(AppDbContext db, TimeProvider clock) : ILoginTokenRepository
{
    public async Task AddAsync(LoginToken token, CancellationToken cancellationToken = default)
    {
        db.LoginTokens.Add(token);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<LoginToken?> GetActiveByHashAsync(string email, string purpose, string codeHash, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        return await db.LoginTokens.FirstOrDefaultAsync(t =>
            t.Email == email && t.Purpose == purpose && t.CodeHash == codeHash
            && t.ConsumedAt == null && t.ExpiresAt > now, cancellationToken);
    }

    public async Task<LoginToken?> GetLatestActiveAsync(string email, string purpose, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        return await db.LoginTokens
            .Where(t => t.Email == email && t.Purpose == purpose && t.ConsumedAt == null && t.ExpiresAt > now)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<int> CountFailedAttemptsSinceAsync(string email, string purpose, DateTimeOffset since, CancellationToken cancellationToken = default) =>
        // (int?) + ?? 0 so SUM over zero rows yields 0 rather than throwing on a NULL aggregate.
        await db.LoginTokens
            .Where(t => t.Email == email && t.Purpose == purpose && t.CreatedAt >= since)
            .SumAsync(t => (int?)t.AttemptCount, cancellationToken) ?? 0;

    public async Task UpdateAsync(LoginToken token, CancellationToken cancellationToken = default)
    {
        db.LoginTokens.Update(token);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> TryConsumeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // The WHERE ConsumedAt IS NULL is the race guard: Postgres serializes the two UPDATEs on the row, so
        // exactly one caller sees affected == 1 and may issue a session (LB-AUTH-3).
        var affected = await db.LoginTokens
            .Where(t => t.Id == id && t.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, (DateTimeOffset?)clock.GetUtcNow()), cancellationToken);
        return affected == 1;
    }

    public async Task IncrementAttemptAsync(Guid id, CancellationToken cancellationToken = default) =>
        // Server-side increment — never count++ on a value read earlier (LB-AUTH-2).
        await db.LoginTokens
            .Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.AttemptCount, t => t.AttemptCount + 1), cancellationToken);

    public async Task InvalidateActiveAsync(string email, string purpose, CancellationToken cancellationToken = default) =>
        // Set-based: consume every still-active credential in one statement (standalone commit).
        await db.LoginTokens
            .Where(t => t.Email == email && t.Purpose == purpose && t.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ConsumedAt, (DateTimeOffset?)clock.GetUtcNow()), cancellationToken);
}
