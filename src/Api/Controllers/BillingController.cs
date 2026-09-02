using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Vuelto.Api.Authentication;
using Vuelto.Api.Models;
using Vuelto.Api.Services;
using Vuelto.Core.Abstractions;
using Vuelto.Core.Authorization;
using Vuelto.Core.Entities;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Controllers;

/// <summary>
/// Billing &amp; subscriptions (ADR-006). A <b>platform</b> controller — billing is reusable chassis,
/// not an app feature — so it lives alongside the household controllers and reuses the
/// owner/membership gate from <see cref="TenantApiControllerBase"/> rather than re-implementing it.
/// </summary>
[ApiController]
[Route("api/billing")]
public class BillingController(
    ITenantRepository tenants,
    IBillingService billing,
    IRepository<Subscription> subscriptions,
    IQuotaService quota,
    TimeProvider clock) : TenantApiControllerBase(tenants)
{
    /// <summary>
    /// The tenant's billing state for the billing page (owner only, BILLING-8): active plan
    /// (fail-closed to Free, exactly like entitlements), raw subscription status, seat usage vs the
    /// plan limit, and whether a provider customer exists (gates the portal button).
    /// </summary>
    [HttpGet]
    [RequireTenantPermission(Permission.ManageBilling, "Only the household owner can manage billing")]
    public async Task<IActionResult> Summary(CancellationToken cancellationToken)
    {
        var membership = await GetMembershipAsync(cancellationToken);
        if (membership is null)
            return InvalidToken();

        var subscription = await subscriptions.Query().FirstOrDefaultAsync(cancellationToken);
        var seats = await quota.GetSeatUsageAsync(cancellationToken);

        return Ok(new BillingSummaryResponse
        {
            PlanKey = EntitlementService.ResolvePlanKey(subscription, clock.GetUtcNow()),
            Status = subscription?.Status ?? "none",
            CurrentPeriodEnd = subscription?.CurrentPeriodEnd,
            Seats = new BillingSeats { Used = seats.Used, Limit = seats.Limit },
            HasSubscription = !string.IsNullOrEmpty(subscription?.StripeCustomerId),
        });
    }

    /// <summary>Starts a hosted checkout to subscribe the tenant to a paid plan (owner only).</summary>
    [HttpPost("checkout")]
    [RequireTenantPermission(Permission.ManageBilling, "Only the household owner can manage billing")]
    public async Task<IActionResult> Checkout([FromBody] CreateCheckoutRequest request, CancellationToken cancellationToken)
    {
        var membership = await GetMembershipAsync(cancellationToken);
        if (membership is null)
            return InvalidToken();

        var result = await billing.CreateCheckoutAsync(membership.TenantId, request.PlanKey, cancellationToken);
        return result.Outcome switch
        {
            CheckoutOutcome.Created => Ok(new CheckoutResponse { Url = result.Url! }),
            _ => BadRequest(new ErrorResponse("invalid_plan", "Unknown or non-purchasable plan")),
        };
    }

    /// <summary>Opens the hosted billing-management portal for the tenant's subscription (owner only).</summary>
    [HttpPost("portal")]
    [RequireTenantPermission(Permission.ManageBilling, "Only the household owner can manage billing")]
    public async Task<IActionResult> Portal(CancellationToken cancellationToken)
    {
        var membership = await GetMembershipAsync(cancellationToken);
        if (membership is null)
            return InvalidToken();

        var result = await billing.CreatePortalAsync(cancellationToken);
        return result.Outcome switch
        {
            PortalOutcome.Created => Ok(new PortalResponse { Url = result.Url! }),
            _ => BadRequest(new ErrorResponse("no_subscription", "No subscription to manage")),
        };
    }
}
