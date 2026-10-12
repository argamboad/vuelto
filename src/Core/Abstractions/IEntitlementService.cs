namespace Vuelto.Core.Abstractions;

/// <summary>
/// Answers "does the current tenant's plan include this entitlement?" — the server-side gate behind
/// <c>.RequireEntitlement(...)</c> (ADR-006). Reads the tenant's <c>Subscription</c> projection and
/// resolves the active plan, **failing closed to Free** when there is no subscription or it isn't in an
/// active/trialing, unexpired state. Never trust the client for entitlement.
/// </summary>
public interface IEntitlementService
{
    Task<bool> HasAsync(string entitlementKey, CancellationToken cancellationToken = default);
}
