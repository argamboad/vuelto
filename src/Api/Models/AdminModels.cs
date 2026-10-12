using System.Text.Json.Serialization;
using Vuelto.Core.Repositories;

namespace Vuelto.Api.Models;

public record AdminTenantSummaryResponse
{
    [JsonPropertyName("id")] public required Guid Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("member_count")] public required int MemberCount { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }

    public static AdminTenantSummaryResponse From(TenantSummary t) =>
        new() { Id = t.Id, Name = t.Name, MemberCount = t.MemberCount, CreatedAt = t.CreatedAt };
}

public record AdminTenantDetailResponse
{
    [JsonPropertyName("id")] public required Guid Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("members")] public required IReadOnlyList<TenantMemberResponse> Members { get; init; }
    [JsonPropertyName("subscription_status")] public required string SubscriptionStatus { get; init; }
    [JsonPropertyName("plan_key")] public required string PlanKey { get; init; }
    /// <summary>True when the subscription is backed by a live provider (Stripe) subscription — staff
    /// plan overrides are refused for these (the provider is the source of truth, ADR-006).</summary>
    [JsonPropertyName("provider_managed")] public required bool ProviderManaged { get; init; }
    [JsonPropertyName("audit_event_count")] public required int AuditEventCount { get; init; }
}

/// <summary>Whether the authenticated caller is platform staff — drives the client's admin nav/gate.</summary>
public record AdminStatusResponse
{
    [JsonPropertyName("is_staff")] public required bool IsStaff { get; init; }
}

/// <summary>Staff announcement to a tenant (ADMIN-3). With no <see cref="UserIds"/> it reaches every
/// member; with a non-empty list it targets just those members (ids that aren't members are ignored).</summary>
public record AdminAnnounceRequest
{
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("body")] public string? Body { get; init; }
    [JsonPropertyName("user_ids")] public IReadOnlyList<Guid>? UserIds { get; init; }
}

public record AdminAnnounceResponse
{
    [JsonPropertyName("notified_count")] public required int NotifiedCount { get; init; }
}

/// <summary>Ack for a platform-wide broadcast (ADMIN-3): the fan-out is queued, not yet delivered.</summary>
public record AdminBroadcastResponse
{
    [JsonPropertyName("status")] public required string Status { get; init; }
}

/// <summary>Staff plan override ("comp"): puts a tenant on a paid plan with no provider subscription —
/// the same projection a completed checkout writes, minus the Stripe ids. Revert is a DELETE.</summary>
public record AdminSetSubscriptionRequest
{
    [JsonPropertyName("plan_key")] public string? PlanKey { get; init; }
}

public record AdminSubscriptionResponse
{
    [JsonPropertyName("plan_key")] public required string PlanKey { get; init; }
    [JsonPropertyName("status")] public required string Status { get; init; }
}

public record ImpersonationResponse
{
    /// <summary>Short-lived access token for the impersonated user. No refresh token is issued.</summary>
    [JsonPropertyName("access_token")] public required string AccessToken { get; init; }
    [JsonPropertyName("expires_in")] public required int ExpiresIn { get; init; }
}
