using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Perezosoft.Api.Services;

/// <summary>
/// Service for user management (creation, lookup, login linking).
/// </summary>
public interface IUserService
{
    /// <summary>
    /// Resolves the account for an OAuth identity.
    /// Lookup order: exact provider login → email match (links the new provider to
    /// the existing account) → create a new account (with a fresh tenant).
    /// </summary>
    // emailVerified defaults to false (fail-closed) — a caller that forgets the flag
    // must NOT silently bypass the takeover guard.
    Task<User> GetOrCreateUserAsync(string email, string providerUserId, string provider,
        string? displayName = null, bool emailVerified = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches an OAuth identity to an existing account (explicit linking —
    /// email match not required).
    /// </summary>
    Task<LinkLoginResult> LinkLoginAsync(Guid userId, string provider, string providerUserId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the account for a verified email (passwordless sign-in via magic
    /// link or OTP). Creates a fresh account + tenant when none exists; no external
    /// login row is attached. Email ownership is proven by the redemption, so the
    /// account is marked email-verified.
    /// </summary>
    Task<User> GetOrCreateByEmailAsync(string email, string? displayName = null, CancellationToken cancellationToken = default);

    /// <summary>Gets a user by ID.</summary>
    Task<User?> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Updates the user's preferred UI language (null clears it).</summary>
    Task UpdateLocaleAsync(Guid userId, string? locale, CancellationToken cancellationToken = default);

    /// <summary>Updates the user's preferred UI theme (null = follow the OS scheme).</summary>
    Task UpdateThemeAsync(Guid userId, string? theme, CancellationToken cancellationToken = default);
}

/// <summary>
/// An unverified email claim matched an existing account — refusing the merge
/// blocks the credential-attachment takeover.
/// </summary>
public class UnverifiedEmailConflictException(string email)
    : Exception($"Email '{email}' matches an existing account but the provider did not verify it");

public enum LinkLoginResult
{
    Linked,
    AlreadyLinkedToSameAccount,
    OwnedByAnotherAccount
}

public class UserService(
    IUserRepository repository,
    ITenantRepository tenants,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<UserService> logger) : IUserService
{
    public async Task<User> GetOrCreateUserAsync(string email, string providerUserId, string provider,
        string? displayName = null, bool emailVerified = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email cannot be empty", nameof(email));
        if (string.IsNullOrWhiteSpace(providerUserId))
            throw new ArgumentException("Provider user ID cannot be empty", nameof(providerUserId));
        if (string.IsNullOrWhiteSpace(provider))
            throw new ArgumentException("Provider cannot be empty", nameof(provider));

        // Normalize email so the same address in different casing resolves to one
        // account — stored and matched lower-cased.
        email = email.Trim().ToLowerInvariant();

        // 1. Known provider identity → existing account
        var existingUser = await repository.GetByLoginAsync(provider, providerUserId, cancellationToken);
        if (existingUser != null)
        {
            await RefreshDisplayNameAsync(existingUser, displayName, cancellationToken);
            logger.LogInformation("User found by login: {Email} (provider: {Provider})", email, provider);
            return existingUser;
        }

        // 2. Same email from a new provider → same account; link the identity.
        // Guard: an UNVERIFIED email claim must never attach a new credential to an
        // existing account (takeover vector) — refuse outright.
        var userByEmail = await repository.GetByEmailAsync(email, cancellationToken);
        if (userByEmail != null && !emailVerified)
        {
            logger.LogWarning("Refused unverified-email merge for {Email} via {Provider}", email, provider);
            throw new UnverifiedEmailConflictException(email);
        }
        if (userByEmail != null)
        {
            await repository.AddLoginAsync(new UserLogin
            {
                Id = Guid.CreateVersion7(),
                UserId = userByEmail.Id,
                Provider = provider,
                ProviderUserId = providerUserId
            }, cancellationToken);
            await RefreshDisplayNameAsync(userByEmail, displayName, cancellationToken);

            logger.LogInformation("Linked {Provider} login to existing account: {Email} (userId: {UserId})",
                provider, email, userByEmail.Id);
            return userByEmail;
        }

        // 3. Brand new user — create the tenant ("household of one"), the user, and an
        // owner membership linking them, atomically.
        var trimmedName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        var newUser = new User
        {
            Id = Guid.CreateVersion7(),
            Email = email,
            DisplayName = trimmedName,
            EmailVerified = emailVerified
        };
        newUser.Logins.Add(new UserLogin
        {
            Id = Guid.CreateVersion7(),
            UserId = newUser.Id,
            Provider = provider,
            ProviderUserId = providerUserId
        });

        var createdUser = await CreateUserWithTenantAsync(newUser, trimmedName, cancellationToken);

        logger.LogInformation("New user created: {Email} (provider: {Provider}, userId: {UserId})",
            email, provider, createdUser.Id);

        return createdUser;
    }

    public async Task<User> GetOrCreateByEmailAsync(string email, string? displayName = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("Email cannot be empty", nameof(email));

        email = email.Trim().ToLowerInvariant();

        var existing = await repository.GetByEmailAsync(email, cancellationToken);
        if (existing != null)
        {
            await RefreshDisplayNameAsync(existing, displayName, cancellationToken);
            return existing;
        }

        var trimmedName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        var newUser = new User
        {
            Id = Guid.CreateVersion7(),
            Email = email,
            DisplayName = trimmedName,
            EmailVerified = true // ownership proven by redeeming the link/code
        };

        var created = await CreateUserWithTenantAsync(newUser, trimmedName, cancellationToken);
        logger.LogInformation("New passwordless user created: {Email} (userId: {UserId})", email, created.Id);
        return created;
    }

    /// <summary>
    /// Creates a brand-new user together with a fresh "household of one" tenant and
    /// an owner membership linking them. All three rows are written in one
    /// SaveChanges so a half-provisioned account can never persist.
    /// </summary>
    private async Task<User> CreateUserWithTenantAsync(User newUser, string? trimmedName, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var tenantLabel = trimmedName is { Length: > 0 }
            ? trimmedName.Split(' ')[0]
            : newUser.Email.Split('@')[0];

        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            Name = $"{tenantLabel}'s Household",
            CreatedAt = now,
            UpdatedAt = now
        };
        newUser.CreatedAt = now;
        newUser.UpdatedAt = now;

        var membership = new TenantMembership
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            UserId = newUser.Id,
            Role = TenantRoles.Owner,
            JoinedAt = now
        };

        // Tenant + user (+ its OAuth login via the Logins navigation) + owner membership in
        // one transaction so a half-provisioned account can never persist.
        await using var scope = await unitOfWork.BeginTransactionAsync(cancellationToken);
        await tenants.CreateAsync(tenant, cancellationToken);
        await repository.CreateAsync(newUser, cancellationToken);
        await tenants.AddMemberAsync(membership, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return newUser;
    }

    public Task<User?> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken = default) => repository.GetByIdAsync(userId, cancellationToken);

    public async Task UpdateLocaleAsync(Guid userId, string? locale, CancellationToken cancellationToken = default)
    {
        var user = await repository.GetByIdAsync(userId, cancellationToken);
        if (user is null) return;
        user.Locale = locale;
        user.UpdatedAt = clock.GetUtcNow();
        await repository.UpdateAsync(user, cancellationToken);
    }

    public async Task UpdateThemeAsync(Guid userId, string? theme, CancellationToken cancellationToken = default)
    {
        var user = await repository.GetByIdAsync(userId, cancellationToken);
        if (user is null) return;
        user.Theme = theme;
        user.UpdatedAt = clock.GetUtcNow();
        await repository.UpdateAsync(user, cancellationToken);
    }

    public async Task<LinkLoginResult> LinkLoginAsync(Guid userId, string provider, string providerUserId, CancellationToken cancellationToken = default)
    {
        var owner = await repository.GetByLoginAsync(provider, providerUserId, cancellationToken);
        if (owner != null)
        {
            return owner.Id == userId
                ? LinkLoginResult.AlreadyLinkedToSameAccount
                : LinkLoginResult.OwnedByAnotherAccount;
        }

        await repository.AddLoginAsync(new UserLogin
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Provider = provider,
            ProviderUserId = providerUserId
        }, cancellationToken);

        logger.LogInformation("Explicitly linked {Provider} login to user {UserId}", provider, userId);
        return LinkLoginResult.Linked;
    }

    private async Task RefreshDisplayNameAsync(User user, string? displayName, CancellationToken cancellationToken = default)
    {
        // A provided name refreshes the stored one; null/blank never erases it.
        var trimmed = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        if (trimmed != null && trimmed != user.DisplayName)
        {
            user.DisplayName = trimmed;
            user.UpdatedAt = clock.GetUtcNow();
            await repository.UpdateAsync(user, cancellationToken);
        }
    }
}
