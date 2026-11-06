using Microsoft.EntityFrameworkCore;
using Perezosoft.Core.Abstractions;
using Vuelto.Host.Entities;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Vuelto.Host.Features.Email;

/// <summary>EMAIL-2: a mailbox connection is the user's own PII (ADR-V002) — erased with the account (GDPR-2), never with a household.</summary>
public sealed class EmailConnectionUserDataContributor(IRepository<EmailConnection> connections) : IUserDataContributor
{
    public Task WipeAsync(Guid userId, CancellationToken cancellationToken = default) =>
        connections.Query().Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
}
