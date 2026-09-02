using Microsoft.Extensions.Options;
using Perezosoft.Api.Configuration;
using Perezosoft.Core.Repositories;

namespace Perezosoft.Api.Services;

/// <summary>
/// Decides whether a user is platform staff (ADR-014) by resolving their email and checking the
/// out-of-band config allowlist (<see cref="PlatformAdminSettings.StaffEmails"/>), case-insensitively.
/// Fails closed — an unknown user, or an empty allowlist, is not staff.
/// </summary>
public interface IPlatformStaffService
{
    Task<bool> IsStaffAsync(Guid userId, CancellationToken cancellationToken = default);
}

public sealed class PlatformStaffService(IUserRepository users, IOptions<PlatformAdminSettings> options) : IPlatformStaffService
{
    private readonly HashSet<string> _staff = new(options.Value.StaffEmails, StringComparer.OrdinalIgnoreCase);

    public async Task<bool> IsStaffAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (_staff.Count == 0)
            return false;

        var user = await users.GetByIdAsync(userId, cancellationToken);
        return user is not null && _staff.Contains(user.Email);
    }
}
