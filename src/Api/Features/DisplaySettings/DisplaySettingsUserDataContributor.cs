using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Features.DisplaySettings;

/// <summary>DISPLAY-1: a display preference is the user's own (ADR-V020) — erased with the account (GDPR-2), never with a household.</summary>
public sealed class DisplaySettingsUserDataContributor(IRepository<UserDisplaySettings> settings) : IUserDataContributor
{
    public Task WipeAsync(Guid userId, CancellationToken cancellationToken = default) =>
        settings.Query().Where(s => s.UserId == userId).ExecuteDeleteAsync(cancellationToken);
}
