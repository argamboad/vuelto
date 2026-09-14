using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Vuelto.Api.Controllers;

namespace Vuelto.Api.Configuration;

/// <summary>
/// GATES-1 (ADR-027): when <see cref="BillingSettings.Enabled"/> is false, drops the billing controllers
/// out of the MVC application model so their routes are never built. The result is a genuine <b>404</b> —
/// the same posture PUBAPI/HOOKS get by simply not calling their <c>Map…</c> helpers, which billing could
/// not have because it is attribute-routed through <c>MapControllers()</c>.
/// <para>
/// A per-action filter was the obvious alternative and is weaker: it leaves the routes in the table, and
/// every future billing endpoint has to remember to wear it. Removing the controllers makes the gate
/// hold for endpoints nobody has written yet.
/// </para>
/// <para>
/// The provider webhook is gated too. It is anonymous and signature-authenticated, which is not a reason
/// to accept it while billing is off: with no provider account behind the deployment there is no event
/// worth applying, and an open callback is surface for nothing.
/// </para>
/// </summary>
public sealed class BillingGateConvention(BillingSettings settings) : IApplicationModelConvention
{
    /// <summary>
    /// The controllers that exist only when billing is on. Public because it is an invariant, not a
    /// detail: <c>BillingControllers_AreAllGated</c> asserts every controller routed under
    /// <c>api/billing</c> appears here, which is what lets the provider registration relax its
    /// production fail-fast when the gate is off (a gated-off deployment has no reachable webhook).
    /// </summary>
    public static readonly IReadOnlySet<Type> GatedControllers = new HashSet<Type>
    {
        typeof(BillingController),
        typeof(BillingWebhookController),
    };

    public void Apply(ApplicationModel application)
    {
        if (settings.Enabled) return;

        foreach (var controller in application.Controllers
                     .Where(c => GatedControllers.Contains(c.ControllerType.AsType()))
                     .ToList())
        {
            application.Controllers.Remove(controller);
        }
    }
}
