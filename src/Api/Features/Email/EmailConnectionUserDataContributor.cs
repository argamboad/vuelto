using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.Email;

/// <summary>EMAIL-2: a mailbox connection is the user's own PII (ADR-V002) — erased with the account (GDPR-2), never with a household.</summary>
public sealed class EmailConnectionUserDataContributor(IRepository<EmailConnection> connections) : IUserDataContributor
{
    public Task WipeAsync(Guid userId, CancellationToken cancellationToken = default) =>
        connections.Query().Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
}
