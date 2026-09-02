using System.Text.Json.Serialization;
using Perezosoft.Core.Entities;
using Perezosoft.Core.Repositories;

namespace Perezosoft.Api.Models;

public record RenameTenantRequest
{
    [JsonPropertyName("name")] public string? Name { get; init; }
}

public record TransferOwnershipRequest
{
    [JsonPropertyName("user_id")] public Guid? UserId { get; init; }
}

public record LeaveTenantRequest
{
    [JsonPropertyName("confirm_dissolve")] public bool ConfirmDissolve { get; init; }
}

public record ChangeMemberRoleRequest
{
    /// <summary>The target role — <c>admin</c> or <c>member</c> only (owner is conferred via transfer).</summary>
    [JsonPropertyName("role")] public string? Role { get; init; }
}

public record TenantExportResponse
{
    /// <summary>Signed, time-limited URL to download the export bundle.</summary>
    [JsonPropertyName("download_url")] public required string DownloadUrl { get; init; }
}

public record TenantMemberResponse
{
    [JsonPropertyName("user_id")] public required Guid UserId { get; init; }
    [JsonPropertyName("display_name")] public string? DisplayName { get; init; }
    [JsonPropertyName("email")] public required string Email { get; init; }
    [JsonPropertyName("role")] public required string Role { get; init; }
    [JsonPropertyName("joined_at")] public required DateTimeOffset JoinedAt { get; init; }

    public static TenantMemberResponse From(TenantMemberDetail d) => new()
    {
        UserId = d.UserId,
        DisplayName = d.DisplayName,
        Email = d.Email,
        Role = d.Role,
        JoinedAt = d.JoinedAt
    };
}

public record TenantResponse
{
    [JsonPropertyName("id")] public required Guid Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    /// <summary>The calling user's own role in this tenant.</summary>
    [JsonPropertyName("my_role")] public required string MyRole { get; init; }
    [JsonPropertyName("members")] public required IReadOnlyList<TenantMemberResponse> Members { get; init; }
}

public record CreateInvitationRequest
{
    [JsonPropertyName("email")] public string? Email { get; init; }
}

public record AcceptInvitationRequest
{
    [JsonPropertyName("token")] public string? Token { get; init; }
}

/// <summary>Invitation shown in the list — email/status/expiry only; token never returned.</summary>
public record InvitationResponse
{
    [JsonPropertyName("invitation_id")] public required Guid InvitationId { get; init; }
    [JsonPropertyName("tenant_id")] public required Guid TenantId { get; init; }
    [JsonPropertyName("invited_email")] public required string InvitedEmail { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("expires_at")] public required DateTimeOffset ExpiresAt { get; init; }

    public static InvitationResponse From(TenantInvitation i) => new()
    {
        InvitationId = i.Id,
        TenantId = i.TenantId,
        InvitedEmail = i.InvitedEmail,
        Status = i.Status,
        CreatedAt = i.CreatedAt,
        ExpiresAt = i.ExpiresAt
    };
}

/// <summary>
/// One-time response for create + regenerate — includes the raw token exactly
/// once for the owner to copy. The token is not persisted and will not be
/// returned again.
/// </summary>
public record CreateInvitationResponse
{
    [JsonPropertyName("invitation_id")] public required Guid InvitationId { get; init; }
    [JsonPropertyName("tenant_id")] public required Guid TenantId { get; init; }
    [JsonPropertyName("invited_email")] public required string InvitedEmail { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("token")] public required string Token { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("expires_at")] public required DateTimeOffset ExpiresAt { get; init; }

    public static CreateInvitationResponse From(TenantInvitation i, string rawToken) => new()
    {
        InvitationId = i.Id,
        TenantId = i.TenantId,
        InvitedEmail = i.InvitedEmail,
        Status = i.Status,
        Token = rawToken,
        CreatedAt = i.CreatedAt,
        ExpiresAt = i.ExpiresAt
    };
}
