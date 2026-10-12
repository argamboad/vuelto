using Vuelto.Api.Configuration;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Services;

/// <summary>
/// Refused account creation: the address is not on the signup green list and holds no invitation that
/// admits it (GATES-2, ADR-027). Thrown from the account-creation choke point, so every sign-in path
/// gets it and each surfaces it in its own idiom.
/// </summary>
public class SignupNotAllowedException(string email)
    : Exception($"Email '{email}' may not create an account: the deployment's signup green list does not admit it");

/// <summary>
/// Decides who may create an account (GATES-2, ADR-027). The rule in one line: <b>the green list decides
/// who may FOUND a household</b>; inside a household owned by a green-listed person, membership is that
/// owner's business, bounded only by the seat cap.
/// </summary>
public interface ISignupGate
{
    /// <summary>
    /// Whether <paramref name="email"/> may create an account. Always true when no green list is
    /// configured. Never consulted for an existing account — the gate is on creation only.
    /// </summary>
    Task<bool> IsAllowedAsync(string email, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISignupGate"/>
public class SignupGate(
    SignupSettings settings,
    ITenantInvitationRepository invitations,
    ITenantRepository tenants,
    TimeProvider clock,
    ILogger<SignupGate> logger) : ISignupGate
{
    public async Task<bool> IsAllowedAsync(string email, CancellationToken cancellationToken = default)
    {
        if (!settings.IsRestricted) return true;      // empty green list ⇒ open (the shipped default)
        if (settings.Allows(email)) return true;      // a founder

        // Otherwise the only way in is an invitation into someone else's household. Note what is NOT
        // consulted: who clicked invite. Keying on the household's OWNER is what lets a non-listed admin
        // member invite into their owner's household, while keeping a household whose owner is not listed
        // from admitting anyone new — including the tenant-of-one that TenantService.ReHomeAsync hands
        // someone who leaves.
        var pending = await invitations.GetValidByEmailAcrossTenantsAsync(email, clock.GetUtcNow(), cancellationToken);
        foreach (var invitation in pending)
        {
            if (await OwnerIsGreenListedAsync(invitation.TenantId, cancellationToken))
                return true;
        }

        logger.LogInformation("Refused signup for an address outside the green list (GATES-2).");
        return false;
    }

    private async Task<bool> OwnerIsGreenListedAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var members = await tenants.GetMemberDetailsAsync(tenantId, cancellationToken);
        var owner = members.FirstOrDefault(m => string.Equals(m.Role, TenantRoles.Owner, StringComparison.OrdinalIgnoreCase));
        return owner is not null && settings.Allows(owner.Email);
    }
}
