using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vuelto.Api.Configuration;

namespace Vuelto.Api.Controllers;

/// <summary>
/// Which config-gated surfaces this deployment has switched on (GATES-1, ADR-027). The Blazor client
/// cannot read server configuration, so it asks — this is what lets the header hide the billing link and
/// <c>/billing</c> refuse to render when billing is off.
/// <para>
/// <b>Anonymous by design.</b> The header renders before anything is known about the caller, and whether
/// a deployment sells subscriptions is not a secret. It is also <b>not</b> the enforcement: the billing
/// routes are removed from the route table entirely when the gate is off
/// (<see cref="BillingGateConvention"/>), so a client that ignores this probe gains nothing.
/// </para>
/// Deliberately does not report the signup green list (GATES-2): the client has no use for it, and
/// publishing it would tell a stranger whether the deployment is currently accepting new accounts.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/features")]
public class FeaturesController(BillingSettings billing) : ControllerBase
{
    /// <summary>The gated surfaces that are on. Flat and additive — new gates get a new key.</summary>
    [HttpGet]
    public IActionResult Get() => Ok(new FeaturesResponse(billing.Enabled));

    /// <param name="Billing">True when the billing surface exists (GATES-1).</param>
    public sealed record FeaturesResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("billing")] bool Billing);
}
