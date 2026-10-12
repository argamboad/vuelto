using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Vuelto.Api.Authentication;
using Vuelto.Api.Models;
using Vuelto.Api.Services;
using Vuelto.Core.Authorization;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Controllers;

/// <summary>
/// Tenant ("household") invitations. Owners or admins (Permission.ManageMembers) invite by email,
/// list, regenerate and revoke pending invites; any authenticated user accepts with a token to join.
/// The raw token is surfaced once on create/regenerate (also emailed to the
/// invitee); the list never returns tokens.
/// </summary>
[ApiController]
[Route("api/household/invitations")]
public class HouseholdInvitationsController(
    ITenantInvitationService service,
    ITenantRepository tenants) : TenantApiControllerBase(tenants)
{
    /// <summary>Creates an invitation (owner or admin). Returns the raw token once and emails it.</summary>
    [HttpPost]
    [RequireTenantPermission(Permission.ManageMembers, "Only the household owner or admin can invite members")]
    public async Task<IActionResult> Create([FromBody] CreateInvitationRequest request, CancellationToken cancellationToken)
    {
        var membership = await GetMembershipAsync(cancellationToken);
        if (membership == null)
            return InvalidToken();

        var result = await service.CreateAsync(membership.TenantId, membership.UserId, request.Email ?? "", cancellationToken);
        return result.Status switch
        {
            InviteCreateStatus.Created => Created(
                $"/api/household/invitations/{result.Invitation!.Id}",
                CreateInvitationResponse.From(result.Invitation, result.RawToken!)),
            InviteCreateStatus.AlreadyMember => Conflict(
                new ErrorResponse("already_member", "That email is already a member of this household")),
            // Seat quota hit (BILLING-5): 402 with an upgrade pointer, mirroring the entitlement gate.
            InviteCreateStatus.SeatLimitReached => StatusCode(StatusCodes.Status402PaymentRequired,
                new ErrorResponse("seat_limit_reached",
                    "Your plan's seat limit is reached — upgrade to invite more members.")),
            _ => BadRequest(new ErrorResponse("invalid_request", "A valid email is required")),
        };
    }

    /// <summary>Lists the tenant's pending invitations (owner or admin). Token is not returned.</summary>
    [HttpGet]
    [RequireTenantPermission(Permission.ManageMembers, "Only the household owner or admin can view invitations")]
    public async Task<IActionResult> GetPending(CancellationToken cancellationToken)
    {
        var membership = await GetMembershipAsync(cancellationToken);
        if (membership == null)
            return InvalidToken();

        var pending = await service.GetPendingAsync(membership.TenantId, cancellationToken);
        return Ok((IReadOnlyList<InvitationResponse>)pending.Select(InvitationResponse.From).ToList());
    }

    /// <summary>Regenerates the token for a pending invitation (owner or admin). Issues a new raw token once.</summary>
    [HttpPost("{id:guid}/regenerate")]
    [RequireTenantPermission(Permission.ManageMembers, "Only the household owner or admin can regenerate invitation tokens")]
    public async Task<IActionResult> Regenerate(Guid id, CancellationToken cancellationToken)
    {
        var membership = await GetMembershipAsync(cancellationToken);
        if (membership == null)
            return InvalidToken();

        var result = await service.RegenerateAsync(membership.TenantId, id, membership.UserId, cancellationToken);
        return result.Status switch
        {
            InviteRegenerateStatus.Regenerated => Ok(
                CreateInvitationResponse.From(result.Invitation!, result.RawToken!)),
            InviteRegenerateStatus.NotFound => NotFound(
                new ErrorResponse("invitation_not_found", "Invitation not found")),
            _ => BadRequest(
                new ErrorResponse("invitation_not_pending", "Only pending invitations can be regenerated")),
        };
    }

    /// <summary>Revokes a pending invitation (owner or admin).</summary>
    [HttpDelete("{id:guid}")]
    [RequireTenantPermission(Permission.ManageMembers, "Only the household owner or admin can revoke invitations")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken cancellationToken)
    {
        var membership = await GetMembershipAsync(cancellationToken);
        if (membership == null)
            return InvalidToken();

        var found = await service.RevokeAsync(membership.TenantId, id, cancellationToken);
        return found
            ? NoContent()
            : NotFound(new ErrorResponse("invitation_not_found", "Invitation not found"));
    }

    /// <summary>Accepts an invitation by token, joining the tenant (any member).</summary>
    [HttpPost("accept")]
    public async Task<IActionResult> Accept([FromBody] AcceptInvitationRequest request, CancellationToken cancellationToken)
    {
        if (CurrentUserId is not { } userId)
            return InvalidToken();

        var status = await service.AcceptAsync(userId, request.Token ?? "", cancellationToken);
        return status switch
        {
            AcceptStatus.Joined => NoContent(),
            AcceptStatus.AlreadyMember => Conflict(
                new ErrorResponse("already_member", "You are already a member of this household")),
            AcceptStatus.MustTransferFirst => BadRequest(
                new ErrorResponse("must_transfer_first", "Transfer ownership before joining another household")),
            // Seat re-check at accept (BILLING-9): the tenant downgraded below its reserved seats —
            // same code/status as the create-path gate so clients handle one shape.
            AcceptStatus.SeatLimitReached => StatusCode(StatusCodes.Status402PaymentRequired,
                new ErrorResponse("seat_limit_reached",
                    "This household has reached its plan's member limit — the owner must upgrade before you can join.")),
            AcceptStatus.WouldAbandonData => BadRequest(
                new ErrorResponse("would_abandon_data", "Your household has data — transfer or remove it before joining another")),
            AcceptStatus.NoHousehold => BadRequest(
                new ErrorResponse("invalid_request", "Caller has no household")),
            _ => BadRequest(
                new ErrorResponse("invitation_invalid", "Invitation is invalid or has expired")),
        };
    }
}
