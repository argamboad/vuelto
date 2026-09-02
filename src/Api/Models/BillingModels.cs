using System.Text.Json.Serialization;

namespace Perezosoft.Api.Models;

/// <summary>Request to start a hosted checkout for a paid plan.</summary>
public record CreateCheckoutRequest
{
    [JsonPropertyName("plan_key")] public string? PlanKey { get; init; }
}

/// <summary>The hosted checkout URL the client should redirect to.</summary>
public record CheckoutResponse
{
    [JsonPropertyName("url")] public required string Url { get; init; }
}

/// <summary>The hosted billing-portal URL the client should redirect to.</summary>
public record PortalResponse
{
    [JsonPropertyName("url")] public required string Url { get; init; }
}

/// <summary>The tenant's billing state for the billing page (BILLING-8). Plan is fail-closed.</summary>
public record BillingSummaryResponse
{
    [JsonPropertyName("plan_key")] public required string PlanKey { get; init; }
    /// <summary>Raw subscription status ("none" when the tenant never subscribed).</summary>
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("current_period_end")] public DateTimeOffset? CurrentPeriodEnd { get; init; }
    [JsonPropertyName("seats")] public required BillingSeats Seats { get; init; }
    /// <summary>True when a provider customer exists — gates the portal button.</summary>
    [JsonPropertyName("has_subscription")] public required bool HasSubscription { get; init; }
}

public record BillingSeats
{
    [JsonPropertyName("used")] public required int Used { get; init; }
    [JsonPropertyName("limit")] public int? Limit { get; init; }
}
