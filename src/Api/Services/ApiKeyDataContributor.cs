using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Services;

/// <summary>
/// Makes PUBAPI (ADR-015) participate in tenant dissolve + export (LB-TEN-1). <see cref="ApiKey"/> is
/// <c>ITenantScoped</c> but has no FK to <c>Tenants</c> (ADR-003 plain-Guid tenancy), so nothing cascades —
/// without this contributor a dissolved tenant's hashed key credentials survive as orphan rows (a
/// credential-hygiene + GDPR-erasure defect) and the export silently omits them. Wipe deletes the keys;
/// export lists their non-secret metadata only — <b>never the <see cref="ApiKey.KeyHash"/></b>.
/// <para>
/// <see cref="HasDataAsync"/> is <c>false</c>: API keys are operational credentials, not tenant content, so
/// (like billing plumbing) they must not trip the "would abandon data" guard that stops a solo owner from
/// leaving — they are cleaned up automatically instead.
/// </para>
/// </summary>
public sealed class ApiKeyDataContributor(IRepository<ApiKey> apiKeys) : ITenantDataContributor
{
    public Task<bool> HasDataAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public async Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        // Query() (not QueryAllTenants): the dissolve enters the target tenant (RLS-2/T6), so the filter
        // scopes this to it; the explicit predicate is fail-safe. Composing QueryAllTenants() with a
        // set-based write is banned (RLS-4/T7) — the tag can't sanction it.
        await apiKeys.Query()
            .Where(k => k.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);

    public string ExportKey => "api_keys";

    // Identifying metadata only — never the key hash (the raw key was never stored, and the hash is a
    // secret-equivalent lookup credential): same rule as billing ids / token hashes (ADR-011).
    public async Task<object?> ExportAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await apiKeys.QueryAllTenants()
            .Where(k => k.TenantId == tenantId)
            .OrderBy(k => k.CreatedAt)
            .Select(k => new
            {
                k.Id, k.Name, k.Prefix, k.Scopes,
                k.CreatedAt, k.LastUsedAt, k.ExpiresAt, k.RevokedAt,
            })
            .ToListAsync(cancellationToken);
}
