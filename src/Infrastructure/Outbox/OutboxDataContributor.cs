using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Infrastructure.Outbox;

/// <summary>
/// Makes the outbox participate in tenant dissolve (v4 audit H6, R145). The outbox is platform infrastructure,
/// but a row that names a tenant carries that tenant's content — an invitation, an export and its attachment, a
/// webhook body — and before this nothing removed it, so a dissolved household's document and recipient
/// outlived the household. Removes the tenant's rows of every type whose handler says it
/// <see cref="IOutboxHandler.DissolvesWithItsTenant"/>, in any status: pending mail is never sent, and a sent
/// row's record goes too. A type no registered handler claims is kept — never guess an effect away; the one
/// that matters, <c>billing.cancel</c>, is queued by this very dissolve and must still run.
/// <para>
/// Not content, so <see cref="HasDataAsync"/> is false (it never blocks a solo owner's join), and nothing is
/// exported: a message queue is not the household's data, its effects are.
/// </para>
/// </summary>
public sealed class OutboxDataContributor(IRepository<OutboxMessage> messages, IEnumerable<IOutboxHandler> handlers)
    : ITenantDataContributor
{
    private readonly string[] _dissolving = [.. handlers.Where(h => h.DissolvesWithItsTenant).Select(h => h.Type)];

    public Task<bool> HasDataAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public async Task WipeAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        // OutboxMessage isn't ITenantScoped (no query filter, no RLS policy), so Query() reaches every row and
        // the TenantId predicate is the whole scope. Set-based, so it enlists in the dissolve's transaction.
        await messages.Query()
            .Where(m => m.TenantId == tenantId && _dissolving.Contains(m.Type))
            .ExecuteDeleteAsync(cancellationToken);

    public string ExportKey => "outbox";

    public Task<object?> ExportAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult<object?>(null);
}
