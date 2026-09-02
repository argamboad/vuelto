namespace Vuelto.Core.Entities;

/// <summary>
/// A single-use MFA recovery code (MFA-1, ADR-012), stored <b>only as a hash</b> (<c>ITokenHasher</c>) —
/// the raw code is shown to the user exactly once at enrollment. Consumed by stamping
/// <see cref="UsedAt"/>. User-scoped identity data — wiped by account erasure (GDPR-2).
/// </summary>
public class MfaRecoveryCode
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }

    /// <summary>Hash of the raw recovery code. Never store the plaintext.</summary>
    public required string CodeHash { get; set; }

    /// <summary>When the code was consumed, or null if still usable.</summary>
    public DateTimeOffset? UsedAt { get; set; }
}
