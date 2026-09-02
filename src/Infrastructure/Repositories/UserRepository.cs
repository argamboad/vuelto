using Microsoft.EntityFrameworkCore;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;
using Perezosoft.Infrastructure.Persistence;

namespace Perezosoft.Infrastructure.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IUserRepository"/>.
/// Encapsulates all user data access logic.
/// </summary>
public class UserRepository(AppDbContext db) : IUserRepository
{
    public async Task<User?> GetByLoginAsync(string provider, string providerUserId, CancellationToken cancellationToken = default)
    {
        return await db.UserLogins
            .Where(l => l.Provider == provider && l.ProviderUserId == providerUserId)
            .Select(l => l.User)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<User?> GetByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        return await db.Users.FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
    }

    public async Task<User?> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await db.Users.FindAsync([userId], cancellationToken);
    }

    public async Task<IReadOnlyList<Guid>> GetAllUserIdsAsync(CancellationToken cancellationToken = default)
    {
        // Users are a global identity (not ITenantScoped), so this enumerates everyone — the caller
        // (a platform-staff broadcast) is authorized platform-wide.
        return await db.Users.Select(u => u.Id).ToListAsync(cancellationToken);
    }

    public async Task<User> CreateAsync(User user, CancellationToken cancellationToken = default)
    {
        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    public async Task<User> UpdateAsync(User user, CancellationToken cancellationToken = default)
    {
        db.Users.Update(user);
        await db.SaveChangesAsync(cancellationToken);
        return user;
    }

    public async Task AddLoginAsync(UserLogin login, CancellationToken cancellationToken = default)
    {
        db.UserLogins.Add(login);
        await db.SaveChangesAsync(cancellationToken);
    }
}
