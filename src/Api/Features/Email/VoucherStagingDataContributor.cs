using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Email;

/// <summary>Tenant-data hook for the staged drafts and their dedup tombstones: both are household data — wiped on dissolve, drafts exported.</summary>
public sealed class VoucherStagingDataContributor(IRepository<PendingVoucher> pending, IRepository<IngestedVoucher> ingested) : ITenantDataContributor
{
    /// <summary>A constant — the platform's export-key gate reads it from an uninitialized instance.</summary>
    public string ExportKey => "pending_vouchers";

    public Task<bool> HasDataAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        pending.QueryAllTenants().AnyAsync(p => p.TenantId == tenantId, cancellationToken);

    public async Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await ingested.Query().Where(i => i.TenantId == tenantId).ExecuteDeleteAsync(cancellationToken);
        await pending.Query().Where(p => p.TenantId == tenantId).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<object?> ExportAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var rows = await pending.QueryAllTenants()
            .Where(p => p.TenantId == tenantId)
            .OrderByDescending(p => p.ReceivedAt ?? p.CreatedAt)
            .Select(p => new { p.Id, p.ParsedBank, p.Merchant, p.Amount, p.Currency, p.Date, p.Authorization, p.Reference, p.TransactionType, p.Status, p.ConfirmedTransactionId, p.ReceivedAt, p.CreatedAt })
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? null : rows;
    }
}
