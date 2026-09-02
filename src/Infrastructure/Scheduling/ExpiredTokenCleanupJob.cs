using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Infrastructure.Scheduling;

/// <summary>
/// Reference scheduled job (ADR-007): deletes expired passwordless and refresh tokens so the auth
/// tables don't grow without bound. Only rows whose <c>ExpiresAt</c> is in the past are removed —
/// a revoked-but-unexpired refresh token is kept because its hash still backs rotation/replay
/// detection until it expires. Copy this shape for trial-expiry sweeps, quota resets, etc.
/// </summary>
public sealed class ExpiredTokenCleanupJob(AppDbContext db, TimeProvider clock) : IScheduledJob
{
    public string Name => "expired-token-cleanup";
    public TimeSpan Interval => TimeSpan.FromHours(1);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        await db.LoginTokens.Where(t => t.ExpiresAt < now).ExecuteDeleteAsync(cancellationToken);
        await db.RefreshTokens.Where(t => t.ExpiresAt < now).ExecuteDeleteAsync(cancellationToken);
    }
}
