using Perezosoft.Core.Entities;

namespace Perezosoft.Core.Repositories;

/// <summary>Tenant invitations.</summary>
public interface ITenantInvitationRepository
{
    Task<TenantInvitation> CreateAsync(TenantInvitation invitation, CancellationToken cancellationToken = default);

    /// <summary>Unscoped read — callers distinguish 404 from 403.</summary>
    Task<TenantInvitation?> GetByIdUnscopedAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Pending invites for a tenant, newest first.</summary>
    Task<List<TenantInvitation>> GetPendingForTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>An existing pending invite for (tenant, email), if any (dedup).</summary>
    Task<TenantInvitation?> GetPendingByEmailAsync(Guid tenantId, string email, CancellationToken cancellationToken = default);

    /// <summary>Lookup by the hashed token (any status — caller validates). Pass the hash, not the raw token.</summary>
    Task<TenantInvitation?> GetByTokenHashAsync(string tokenHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically flips a pending invitation to accepted — the DB-level guard for
    /// concurrent accepts. Returns true if the row was pending and was updated;
    /// false if another request already consumed it (race lost — caller must abort
    /// its transaction scope so the membership move rolls back).
    /// </summary>
    Task<bool> TryAcceptAsync(Guid id, CancellationToken cancellationToken = default);

    Task<TenantInvitation> UpdateAsync(TenantInvitation invitation, CancellationToken cancellationToken = default);
}
