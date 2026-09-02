using Microsoft.EntityFrameworkCore;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Perezosoft.Api.Services;

/// <summary>A freshly created key: the persisted row plus the one-time raw value (never stored).</summary>
public sealed record ApiKeyCreated(ApiKey Key, string RawKey);

/// <summary>Resolved identity behind a valid API key — what the auth handler turns into a principal.</summary>
public sealed record ApiKeyAuthResult(Guid KeyId, Guid TenantId, string Name, IReadOnlySet<string> Scopes);

/// <summary>
/// Manages tenant API keys (PUBAPI, ADR-015). Management (create/list/revoke) runs in the current tenant's
/// scope (owner-gated at the endpoint); <see cref="AuthenticateAsync"/> runs <b>before</b> any tenant scope
/// — the presented key selects its tenant — so it reads across tenants by hash. Only the hash is stored;
/// the raw key (prefixed <c>pk_</c>) is returned once at creation.
/// </summary>
public interface IApiKeyService
{
    /// <summary>Mints a key; null if scopes were provided but none are recognized (reject, don't grant all).</summary>
    Task<ApiKeyCreated?> CreateAsync(Guid createdByUserId, string name, IEnumerable<string>? scopes, DateTimeOffset? expiresAt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken cancellationToken = default);
    Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Resolves a raw key to its tenant + scopes, or null if unknown/revoked/expired.</summary>
    Task<ApiKeyAuthResult?> AuthenticateAsync(string rawKey, CancellationToken cancellationToken = default);
}

public sealed class ApiKeyService(
    IRepository<ApiKey> keys,
    ITokenGenerator tokenGenerator,
    ITokenHasher tokenHasher,
    ITenantContext tenantContext,
    TimeProvider clock) : IApiKeyService
{
    private const string RawPrefix = "pk_";

    public async Task<ApiKeyCreated?> CreateAsync(Guid createdByUserId, string name, IEnumerable<string>? scopes, DateTimeOffset? expiresAt, CancellationToken cancellationToken = default)
    {
        var granted = NormalizeScopes(scopes);
        if (granted is null)
            return null; // scopes were provided but none are known — reject rather than grant all

        var raw = RawPrefix + tokenGenerator.GenerateToken();
        var key = new ApiKey
        {
            Name = string.IsNullOrWhiteSpace(name) ? "API key" : name.Trim(),
            KeyHash = tokenHasher.HashToken(raw),
            Prefix = raw[..Math.Min(raw.Length, 12)], // e.g. "pk_ab12cd34" — non-secret, for display
            Scopes = string.Join(',', granted),
            CreatedByUserId = createdByUserId,
            CreatedAt = clock.GetUtcNow(),
            ExpiresAt = expiresAt,
        };
        await keys.AddAsync(key, cancellationToken); // TenantId stamped to the current tenant by the interceptor
        await keys.SaveChangesAsync(cancellationToken);
        return new ApiKeyCreated(key, raw);
    }

    public async Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken cancellationToken = default) =>
        await keys.Query().OrderByDescending(k => k.CreatedAt).ToListAsync(cancellationToken);

    public async Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var key = await keys.Query().FirstOrDefaultAsync(k => k.Id == id, cancellationToken); // tenant-scoped
        if (key is null)
            return false;

        if (key.RevokedAt is null)
        {
            key.RevokedAt = clock.GetUtcNow();
            keys.Update(key);
            await keys.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    public async Task<ApiKeyAuthResult?> AuthenticateAsync(string rawKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawKey) || !rawKey.StartsWith(RawPrefix, StringComparison.Ordinal))
            return null;

        var hash = tokenHasher.HashToken(rawKey);
        var now = clock.GetUtcNow();

        // Pre-tenant-scope: the key itself selects the tenant, so resolve across all tenants by hash.
        var key = await keys.QueryAllTenants().FirstOrDefaultAsync(k => k.KeyHash == hash, cancellationToken);
        if (key is null || !key.IsActive(now))
            return null;

        // Best-effort last-used stamp. Enter the key's tenant so the write is scoped by the RLS policy
        // (ADR-020) to it, instead of relying on auth running tenantless (RLS-8). A tracked update on the
        // already-loaded key (not a QueryAllTenants()+ExecuteUpdate composition, which is banned — RLS-4 —
        // because the tag can't sanction a set-based write) — the stamp is scoped by the entered tenant.
        using (tenantContext.EnterTenant(key.TenantId))
        {
            key.LastUsedAt = now;
            keys.Update(key);
            await keys.SaveChangesAsync(cancellationToken);
        }

        return new ApiKeyAuthResult(key.Id, key.TenantId, key.Name, ApiScopes.Parse(key.Scopes));
    }

    // A null request defaults to all known scopes; a request that names scopes but none are known is
    // REJECTED (null), never silently granted full access — v2 audit SOLID-3.
    private static IReadOnlyList<string>? NormalizeScopes(IEnumerable<string>? scopes)
    {
        if (scopes is null)
            return ApiScopes.All;

        var requested = scopes
            .Select(s => s.Trim().ToLowerInvariant())
            .Where(ApiScopes.All.Contains)
            .Distinct()
            .ToList();
        return requested.Count == 0 ? null : requested; // provided but all-invalid → reject
    }
}
