using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Perezosoft.Api.Services;

namespace Perezosoft.Api.Controllers;

/// <summary>
/// Receives billing-provider webhooks (BILLING-3, ADR-006). A <b>system</b> endpoint — the provider
/// calls it with no JWT — so it does NOT extend <c>TenantApiControllerBase</c>; authenticity is the
/// provider <b>signature</b>, verified inside <see cref="BillingWebhookHandler"/>, which then enters the
/// tenant context to apply the change safely (ADR-003). Anonymous + not rate-limited (the passwordless
/// limiter is opt-in per endpoint).
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/billing/webhook")]
public class BillingWebhookController(BillingWebhookHandler handler, ILogger<BillingWebhookController> logger) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body);
        var payload = await reader.ReadToEndAsync(cancellationToken);
        var signature = Request.Headers["Stripe-Signature"].ToString();

        var result = await handler.HandleAsync(payload, signature, cancellationToken);
        if (result == WebhookResult.InvalidSignature)
        {
            // Anonymous endpoint (GAP-5): a rejected signature is a forged-webhook probe — make it
            // observable with the source IP rather than a silent 400. No payload contents are logged.
            var sourceIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            logger.LogWarning("Rejected billing webhook with an invalid signature from {SourceIp}.", sourceIp);
            // The shared envelope (v3 TR-5/R76) — same `error` key on the wire, plus the standard message.
            return BadRequest(new ErrorResponse("invalid_signature", "The webhook signature is invalid."));
        }

        return Ok(); // Applied / Duplicate / Ignored all acknowledge, so the provider stops retrying
    }
}
