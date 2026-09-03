using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Ledger;

/// <summary>
/// The last tier of the exchange-rate chain (ADR-V006), filled in by this slice: the rate the
/// household froze on its most recently created transaction. Tenant-ambient through <c>Query()</c>.
/// Replaces the FX-1 placeholder registration in <c>Program.cs</c>.
/// </summary>
public sealed class TransactionRecentRateSource(IRepository<Transaction> transactions) : IRecentRateSource
{
    public Task<RecentRate?> GetMostRecentAsync(CancellationToken cancellationToken = default) =>
        transactions.Query()
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => new RecentRate(t.ExchangeRateUsed, t.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken);
}
