using Perezosoft.Core.Entities;

namespace Perezosoft.Core.Repositories;

/// <summary>
/// Repository abstraction for user data access.
/// Provider identities live in user logins; a user can have several.
/// </summary>
public interface IUserRepository
{
    Task<User?> GetByLoginAsync(string provider, string providerUserId, CancellationToken cancellationToken = default);
    Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);
    Task<User?> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Every user id in the system — for a platform-wide staff broadcast fan-out. Users are a
    /// global identity, not tenant-scoped, so this is not filtered by tenant.</summary>
    Task<IReadOnlyList<Guid>> GetAllUserIdsAsync(CancellationToken cancellationToken = default);

    Task<User> CreateAsync(User user, CancellationToken cancellationToken = default);
    Task<User> UpdateAsync(User user, CancellationToken cancellationToken = default);
    Task AddLoginAsync(UserLogin login, CancellationToken cancellationToken = default);
}
