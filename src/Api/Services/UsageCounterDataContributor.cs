using Microsoft.EntityFrameworkCore;
using Perezosoft.Core.Abstractions;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Perezosoft.Api.Services;

/// <summary>
/// Makes metered-usage counters (BILLING-5) participate in tenant dissolve + export (LB-TEN-1).
/// <see cref="UsageCounter"/> is <c>ITenantScoped</c> with no FK cascade, so without this a dissolved
/// tenant's usage rows survive as orphans and the export omits them. Wipe deletes the counters; export
/// lists them (there is nothing secret in a counter).
/// <para>
/// <see cref="HasDataAsync"/> is <c>false</c>: metering is billing plumbing, not tenant content, so it must
/// not block a solo owner from leaving — it is cleaned up automatically instead.
/// </para>
/// </summary>
public sealed class UsageCounterDataContributor(IRepository<UsageCounter> counters) : ITenantDataContributor
{
    public Task<bool> HasDataAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public async Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        // Query() (not QueryAllTenants): the dissolve enters the target tenant (RLS-2/T6), so the filter
        // scopes this to it; the explicit predicate is fail-safe. Composing QueryAllTenants() with a
        // set-based write is banned (RLS-4/T7) — the tag can't sanction it.
        await counters.Query()
            .Where(c => c.TenantId == tenantId)
            .ExecuteDeleteAsync(cancellationToken);

    public string ExportKey => "usage";

    public async Task<object?> ExportAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        await counters.QueryAllTenants()
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Period).ThenBy(c => c.Key)
            .Select(c => new { c.Key, c.Period, c.Count, c.UpdatedAt })
            .ToListAsync(cancellationToken);
}
