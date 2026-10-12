using Vuelto.Core.Entities;

namespace Vuelto.Core.Repositories;

/// <summary>
/// Reads/removes the OAuth identities linked to an account.
/// </summary>
public interface IUserLoginRepository
{
    Task<List<UserLogin>> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default);
    Task<UserLogin?> GetByProviderForUserAsync(Guid userId, string provider, CancellationToken cancellationToken = default);
    Task DeleteAsync(UserLogin login, CancellationToken cancellationToken = default);
}
