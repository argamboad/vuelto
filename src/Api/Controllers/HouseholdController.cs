using Microsoft.AspNetCore.Mvc;
using Perezosoft.Api.Authentication;
using Perezosoft.Api.Models;
using Perezosoft.Api.Services;
using Perezosoft.Core.Authorization;
using Perezosoft.Core.Repositories;

namespace Perezosoft.Api.Controllers;

/// <summary>
/// Tenant ("household") management. Authorization goes through the permission seam (ADR-009):
/// reading needs <see cref="Permission.ViewTenant"/> (every member); rename needs
/// <see cref="Permission.RenameTenant"/> and remove-member needs <see cref="Permission.ManageMembers"/>
/// (owner/admin); transfer needs <see cref="Permission.TransferOwnership"/> and changing a member's
/// role (admin↔member) needs <see cref="Permission.ManageRoles"/> (both owner only). Leave is open to
/// any member. The caller's role is read from the membership, never from the request. Invitations live
/// in <see cref="HouseholdInvitationsController"/>.
/// </summary>
[ApiController]
[Route("api/household")]
public class HouseholdController(
    ITenantService service,
    ITenantRepository tenants,
    ITenantExportService exportService) : TenantApiControllerBase(tenants)
{
    /// <summary>Tenant + caller role + member roster (any member).</summary>
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var membership = await GetMembershipAsync(cancellationToken);
        if (membership == null)
            return InvalidToken();

        var tenant = await Tenants.GetByIdAsync(membership.TenantId, cancellationToken);
        if (tenant == null)
            return NotFound(new ErrorResponse("household_not_found", "Household not found"));

        var members = await Tenants.GetMemberDetailsAsync(membership.TenantId, cancellationToken);
        return Ok(new TenantResponse
        {
            Id = tenant.Id,
            Name = tenant.Name,
            MyRole = membership.Role,
            Members = members.Select(TenantMemberResponse.From).ToList()
        });
    }

    /// <summary>Renames the tenant (owner only).</summary>
    [HttpPut]
    [RequireTenantPermission(Permission.RenameTenant, "Only the household owner can rename the household")]
    public async Task<IActionResult> Rename([FromBody] RenameTenantRequest request, CancellationToken cancellationToken)
    {
        var membership = await GetMembershipAsync(cancellationToken);
        if (membership == null)
            return InvalidToken();

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new ErrorResponse("invalid_request", "Name is required"));

        var renamed = await service.RenameAsync(membership.TenantId, request.Name, cancellationToken);
        if (!renamed)
            return NotFound(new ErrorResponse("household_not_found", "Household not found"));

        var tenant = await Tenants.GetByIdAsync(membership.TenantId, cancellationToken);
        var members = await Tenants.GetMemberDetailsAsync(membership.TenantId, cancellationToken);
        return Ok(new TenantResponse
        {
            Id = tenant!.Id,
            Name = tenant.Name,
            MyRole = membership.Role,
            Members = members.Select(TenantMemberResponse.From).ToList()
        });
    }

    /// <summary>Removes a member (owner only). Cannot remove self.</summary>
    [HttpDelete("members/{userId:guid}")]
    [RequireTenantPermission(Permission.ManageMembers, "Only the household owner can remove members")]
    public async Task<IActionResult> RemoveMember(Guid userId, CancellationToken cancellationToken)
    {
        var membership = await GetMembershipAsync(cancellationToken);
        if (membership == null)
            return InvalidToken();

        // The owner leaves/transfers via the leave/transfer endpoints, not this one.
        if (userId == membership.UserId)
            return BadRequest(new ErrorResponse("invalid_request",
                "You cannot remove yourself — transfer ownership or leave instead"));

        var result = await service.RemoveMemberAsync(membership.TenantId, userId, cancellationToken);
        return result switch
        {
            RemoveMemberResult.Removed => NoContent(),
            RemoveMemberResult.CannotRemoveOwner => BadRequest(new ErrorResponse(
                "cannot_remove_owner", "The household owner can't be removed — transfer ownership first")),
            _ => NotFound(new ErrorResponse("member_not_found", "That user is not a member of this household")),
        };
    }

    /// <summary>Exports the tenant's data as a downloadable bundle (owner only — ExportData).</summary>
    [HttpPost("export")]
    [RequireTenantPermission(Permission.ExportData, "Only the household owner can export the household's data")]
    public async Task<IActionResult> Export(CancellationToken cancellationToken)
    {
        var membership = await GetMembershipAsync(cancellationToken);
        if (membership == null)
            return InvalidToken();

        var url = await exportService.ExportAsync(membership.TenantId, membership.UserId, cancellationToken);
        return Ok(new TenantExportResponse { DownloadUrl = url.ToString() });
    }

    /// <summary>Changes a member's role between admin and member (owner only — ManageRoles).</summary>
    [HttpPut("members/{userId:guid}/role")]
    [RequireTenantPermission(Permission.ManageRoles, "Only the household owner can change member roles")]
    public async Task<IActionResult> ChangeMemberRole(Guid userId, [FromBody] ChangeMemberRoleRequest request, CancellationToken cancellationToken)
    {
        var membership = await GetMembershipAsync(cancellationToken);
        if (membership == null)
            return InvalidToken();

        var result = await service.ChangeMemberRoleAsync(
            membership.TenantId, membership.UserId, userId, request.Role ?? "", cancellationToken);
        return result switch
        {
            ChangeRoleResult.Changed or ChangeRoleResult.Unchanged => NoContent(),
            ChangeRoleResult.TargetNotMember => NotFound(new ErrorResponse(
                "member_not_found", "That user is not a member of this household")),
            ChangeRoleResult.CannotChangeOwner => BadRequest(new ErrorResponse(
                "cannot_change_owner", "The owner's role can't be changed here — use transfer ownership")),
            ChangeRoleResult.CannotChangeSelf => BadRequest(new ErrorResponse(
                "invalid_request", "You cannot change your own role")),
            ChangeRoleResult.InvalidRole => BadRequest(new ErrorResponse(
                "invalid_role", "Role must be 'admin' or 'member'")),
            _ => StatusCode(StatusCodes.Status500InternalServerError,
                new ErrorResponse("internal_error", "Unexpected role-change result")),
        };
    }

    /// <summary>Transfers ownership to an existing member (owner only).</summary>
    [HttpPost("transfer-ownership")]
    [RequireTenantPermission(Permission.TransferOwnership, "Only the household owner can transfer ownership")]
    public async Task<IActionResult> TransferOwnership([FromBody] TransferOwnershipRequest request, CancellationToken cancellationToken)
    {
        var membership = await GetMembershipAsync(cancellationToken);
        if (membership == null)
            return InvalidToken();

        if (request.UserId is not { } targetUserId)
            return BadRequest(new ErrorResponse("invalid_request", "user_id is required"));
        if (targetUserId == membership.UserId)
            return BadRequest(new ErrorResponse("invalid_request", "You already own this household"));

        var result = await service.TransferOwnershipAsync(membership.TenantId, membership.UserId, targetUserId, cancellationToken);
        return result switch
        {
            TransferResult.Transferred => NoContent(),
            TransferResult.TargetNotMember => NotFound(new ErrorResponse("member_not_found", "That user is not a member of this household")),
            TransferResult.ConcurrentModification => Conflict(new ErrorResponse("concurrent_modification", "Ownership was modified by a concurrent request — please retry")),
            _ => StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse("internal_error", "Unexpected transfer result"))
        };
    }

    /// <summary>Leaves the tenant. A sole owner must confirm dissolution; an owner
    /// with members must transfer first.</summary>
    [HttpPost("leave")]
    public async Task<IActionResult> Leave([FromBody] LeaveTenantRequest? request, CancellationToken cancellationToken)
    {
        if (CurrentUserId is not { } userId)
            return InvalidToken();

        var outcome = await service.LeaveAsync(userId, request?.ConfirmDissolve ?? false, cancellationToken);
        return outcome switch
        {
            LeaveOutcome.Left or LeaveOutcome.Dissolved => NoContent(),
            LeaveOutcome.MustTransferFirst => BadRequest(new ErrorResponse(
                "must_transfer_first", "Transfer ownership before leaving — other members remain")),
            LeaveOutcome.ConfirmationRequired => Conflict(new ErrorResponse(
                "confirmation_required",
                "Leaving dissolves this household and permanently deletes its data and pending "
                + "invitations. Re-send with confirm_dissolve=true to proceed.")),
            _ => StatusCode(StatusCodes.Status500InternalServerError,
                new ErrorResponse("internal_error", "Unexpected leave outcome"))
        };
    }
}
