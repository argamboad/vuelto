using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Budget;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Ledger;

/// <summary>The Ledger's implementation of <see cref="ITransactionCards"/>: the slice that owns transactions rewrites them (Arch A8).</summary>
public sealed class TransactionCards(IRepository<Transaction> transactions) : ITransactionCards
{
    public Task<int> MoveAsync(Guid fromCardId, Guid intoCardId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        transactions.Query().Where(t => t.CardId == fromCardId)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.CardId, intoCardId).SetProperty(t => t.UpdatedAt, now), cancellationToken);

    public Task<int> SetPaymentMethodAsync(Guid cardId, string paymentMethod, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        transactions.Query().Where(t => t.CardId == cardId && t.PaymentMethod != paymentMethod)
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.PaymentMethod, paymentMethod).SetProperty(t => t.UpdatedAt, now), cancellationToken);
}
