using Vuelto.Core.Abstractions;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Services;

/// <summary>
/// The shared "dissolve a tenant's data" sequence (DEBT-7): fan out over every
/// <see cref="ITenantDataContributor"/> so each feature wipes its own domain data first, then run the
/// platform's core teardown (<see cref="ITenantRepository.WipeDataAsync"/>). Both the sole-owner leave
/// (<see cref="ITenantService.LeaveAsync"/>) and solo-owner account erasure
/// (<see cref="IAccountErasureService"/>) go through this so the order is identical and defined once.
/// <para>
/// This deliberately does NOT open its own transaction — callers invoke it inside their existing
/// dissolve transaction (with re-home / audit / membership steps) so the whole thing commits atomically.
/// </para>
/// </summary>
public interface ITenantDissolutionService
{
    /// <summary>
    /// Wipes the tenant's data: each contributor first, then the core teardown. Enlists in the caller's
    /// ambient transaction; it does not begin or commit one.
    /// </summary>
    Task DissolveAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

public sealed class TenantDissolutionService(
    IEnumerable<ITenantDataContributor> dataContributors,
    ITenantRepository tenants,
    ITenantContext tenantContext) : ITenantDissolutionService
{
    public async Task DissolveAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        // RLS-2: the teardown below is set-based deletes (each contributor's ExecuteDelete, then the core
        // WipeDataAsync). Under the Postgres RLS backstop (ADR-020) those deletes are gated by the policy on
        // the AMBIENT tenant, and QueryAllTenants() lifts only the EF filter, not the DB wall. So if a caller
        // dissolves a tenant OTHER than its ambient one (e.g. erasing a solo tenant that isn't the caller's
        // JWT-current tenant), every RLS'd delete would silently match zero rows while the non-RLS'd core
        // teardown still succeeds — orphaning the tenant's data (a split-brain). Enter the target tenant so
        // the GUC scopes every delete to it (ADR-003). Scoped to the dissolve only — callers do their own
        // post-dissolve work (re-home, identity-core deletes) in their normal context.
        using (tenantContext.EnterTenant(tenantId))
        {
            // Each feature wipes its domain data first (its contributor), then the platform's core teardown.
            foreach (var contributor in dataContributors)
                await contributor.WipeAsync(tenantId, cancellationToken);
            await tenants.WipeDataAsync(tenantId, cancellationToken);
        }
    }
}
