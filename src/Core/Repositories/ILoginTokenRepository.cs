using Perezosoft.Core.Entities;

namespace Perezosoft.Core.Repositories;

/// <summary>
/// Persists single-use passwordless credentials (magic-link tokens and OTP codes).
/// </summary>
public interface ILoginTokenRepository
{
    Task AddAsync(LoginToken token, CancellationToken cancellationToken = default);

    /// <summary>An unconsumed, unexpired credential matching the exact hash (magic-link lookup).</summary>
    Task<LoginToken?> GetActiveByHashAsync(string email, string purpose, string codeHash, CancellationToken cancellationToken = default);

    /// <summary>The most recent unconsumed, unexpired credential for the email+purpose (OTP lookup).</summary>
    Task<LoginToken?> GetLatestActiveAsync(string email, string purpose, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sum of failed attempts across ALL credentials of this purpose issued to the email since
    /// <paramref name="since"/> — including consumed/rotated-out ones. Drives the cumulative,
    /// resend-proof OTP lockout (a fresh code can't reset the budget).
    /// </summary>
    Task<int> CountFailedAttemptsSinceAsync(string email, string purpose, DateTimeOffset since, CancellationToken cancellationToken = default);

    Task UpdateAsync(LoginToken token, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically claims a single-use credential: stamps <c>ConsumedAt</c> only if it is still null, and
    /// reports whether THIS caller won. Two concurrent redemptions of one credential (email-client prefetch,
    /// double-click) would otherwise both pass a read-then-write <c>ConsumedAt == null</c> check and mint two
    /// sessions (v3 audit LB-AUTH-3) — the caller must issue a session only when this returns true.
    /// </summary>
    Task<bool> TryConsumeAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically increments the credential's failed-attempt counter (server-side <c>+1</c>, no
    /// read-modify-write). Concurrent wrong guesses would otherwise last-writer-wins the increment and let
    /// the brute-force cap be exceeded (v3 audit LB-AUTH-2); evaluate the cap against a re-read of the
    /// persisted total (<see cref="CountFailedAttemptsSinceAsync"/>) AFTER calling this.
    /// </summary>
    Task IncrementAttemptAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Consumes every still-active credential of this purpose — called before issuing a new one.</summary>
    Task InvalidateActiveAsync(string email, string purpose, CancellationToken cancellationToken = default);
}
