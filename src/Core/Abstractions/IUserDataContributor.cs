namespace Vuelto.Core.Abstractions;

/// <summary>
/// A platform concern's contribution to per-<b>user</b> data teardown on account erasure (GDPR-2,
/// ADR-011). Each concern that stores user-keyed PII <em>outside</em> identity-core (MFA, notifications,
/// …) registers one, so <c>AccountErasureService</c> wipes it via the contributor loop instead of a
/// hard-coded list — adding a new user-keyed table never means editing that service. This is the
/// <see cref="ITenantDataContributor"/> pattern applied to the user axis, and it is what keeps GDPR
/// erasure complete as new user-scoped features are added. Runs inside the erasure transaction, so it
/// shares the caller's unit of work — do not commit here.
/// </summary>
public interface IUserDataContributor
{
    /// <summary>Removes this concern's user-keyed data for the given user.</summary>
    Task WipeAsync(Guid userId, CancellationToken cancellationToken = default);
}
