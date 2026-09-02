using System.Text.Json.Serialization;
using Vuelto.Api.Authentication;
using Vuelto.Api.Configuration;
using Vuelto.Api.Services;
using Vuelto.Core.Authorization;
using Vuelto.Core.Entities;

namespace Vuelto.Api.Endpoints;

/// <summary>
/// Outbound webhook management (HOOKS, ADR-016): owner-only routes to register/list/remove subscriptions
/// and fire a **test** delivery. Mapped only when HOOKS is enabled (off ⇒ the routes 404). Real events are
/// emitted by features calling <see cref="IWebhookPublisher.PublishAsync"/> (async via the outbox); the
/// test route delivers synchronously so the owner sees the endpoint's response immediately.
/// </summary>
public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapWebhookManagement(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/webhooks")
            .RequireAuthorization(AuthPolicies.TenantApi)
            .RequirePermission(Permission.ManageWebhooks) // owner-only (RBAC, ADR-009)
            .WithTags("Webhooks");

        group.MapGet("/", async (IWebhookSubscriptionService svc, CancellationToken ct) =>
            Results.Ok((await svc.ListAsync(ct)).Select(WebhookResponse.From).ToList()));

        group.MapPost("/", async (CreateWebhookRequest request, IWebhookSubscriptionService svc, HttpContext http, CancellationToken ct) =>
        {
            var userId = CurrentUserId(http);
            if (userId is null)
                return Results.Unauthorized();

            var created = await svc.CreateAsync(userId.Value, request.Url ?? "", request.EventTypes, ct);
            return created is null
                ? Results.BadRequest(new ErrorResponse("invalid_request", "A valid https URL and at least one known event type are required."))
                : Results.Created($"/api/webhooks/{created.Subscription.Id}", WebhookResponse.FromCreated(created)); // secret shown once
        });

        group.MapDelete("/{id:guid}", async (Guid id, IWebhookSubscriptionService svc, CancellationToken ct) =>
            await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        // Synchronous "send test" (like Stripe's) — POSTs a signed ping and returns the endpoint's status.
        // Also records a WebhookDelivery row (success or failure) so the delivery log / replay are populated
        // in-template — the test-send is the only path that fires a delivery on the shipped platform (HOOKS-2).
        group.MapPost("/{id:guid}/test", async (Guid id, IWebhookSubscriptionService svc, CancellationToken ct) =>
        {
            var result = await svc.SendTestAsync(id, ct);
            if (result is null)
                return Results.NotFound();

            // Don't leak internal DNS/connection detail to the tenant (GAP-3) — it stays in the delivery row.
            return result.TransportFailed
                ? Results.Ok(new { delivered = false, error = "delivery_failed" })
                : Results.Ok(new { delivered = result.Delivered, status_code = result.StatusCode });
        });

        // Delivery log (HOOKS-2): recent attempts for a subscription — the tenant's debug trail.
        group.MapGet("/{id:guid}/deliveries", async (Guid id, IWebhookSubscriptionService svc, CancellationToken ct) =>
            Results.Ok((await svc.ListDeliveriesAsync(id, ct)).Select(WebhookDeliveryResponse.From).ToList()));

        // Replay (HOOKS-2): re-enqueue a past delivery's exact payload (async via the outbox).
        group.MapPost("/deliveries/{deliveryId:guid}/replay", async (Guid deliveryId, IWebhookSubscriptionService svc, CancellationToken ct) =>
            await svc.ReplayAsync(deliveryId, ct) ? Results.Accepted() : Results.NotFound());

        return app;
    }

    private static Guid? CurrentUserId(HttpContext http) => http.User.GetUserId();
}

public sealed record CreateWebhookRequest
{
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("event_types")] public string[]? EventTypes { get; init; }
}

public sealed record WebhookResponse
{
    [JsonPropertyName("id")] public required Guid Id { get; init; }
    [JsonPropertyName("url")] public required string Url { get; init; }
    [JsonPropertyName("event_types")] public required IReadOnlyList<string> EventTypes { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("disabled_at")] public DateTimeOffset? DisabledAt { get; init; }

    /// <summary>The signing secret — present ONLY in the create response, never on list.</summary>
    [JsonPropertyName("secret")] public string? Secret { get; init; }

    public static WebhookResponse From(WebhookSubscription s) => new()
    {
        Id = s.Id,
        Url = s.Url,
        EventTypes = s.EventTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        CreatedAt = s.CreatedAt,
        DisabledAt = s.DisabledAt,
    };

    public static WebhookResponse FromCreated(WebhookCreated created) => From(created.Subscription) with { Secret = created.Secret };
}

public sealed record WebhookDeliveryResponse
{
    [JsonPropertyName("id")] public required Guid Id { get; init; }
    [JsonPropertyName("event_type")] public required string EventType { get; init; }
    [JsonPropertyName("event_id")] public required string EventId { get; init; }
    [JsonPropertyName("success")] public required bool Success { get; init; }
    [JsonPropertyName("status_code")] public int? StatusCode { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("created_at")] public required DateTimeOffset CreatedAt { get; init; }

    public static WebhookDeliveryResponse From(WebhookDelivery d) => new()
    {
        Id = d.Id,
        EventType = d.EventType,
        EventId = d.EventId,
        Success = d.Success,
        StatusCode = d.StatusCode,
        Error = d.Error,
        CreatedAt = d.CreatedAt,
    };
}
