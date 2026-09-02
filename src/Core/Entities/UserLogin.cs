namespace Perezosoft.Core.Entities;

/// <summary>
/// An OAuth provider identity linked to a user account. A user can have
/// multiple logins (e.g. Microsoft and Google with the same email) that all
/// resolve to the same account.
/// </summary>
public class UserLogin
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public required string Provider { get; set; }
    public required string ProviderUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User? User { get; set; }
}
