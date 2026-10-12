using Microsoft.EntityFrameworkCore;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;
using Vuelto.Infrastructure.Persistence;

namespace Vuelto.Infrastructure.Repositories;

public class UserLoginRepository(AppDbContext db) : IUserLoginRepository
{
    public async Task<List<UserLogin>> GetForUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await db.UserLogins
            .Where(l => l.UserId == userId)
            .OrderBy(l => l.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<UserLogin?> GetByProviderForUserAsync(Guid userId, string provider, CancellationToken cancellationToken = default)
    {
        return await db.UserLogins
            .FirstOrDefaultAsync(l => l.UserId == userId && l.Provider == provider, cancellationToken);
    }

    public async Task DeleteAsync(UserLogin login, CancellationToken cancellationToken = default)
    {
        db.UserLogins.Remove(login);
        await db.SaveChangesAsync(cancellationToken);
    }
}
