using Vuelto.Api.Endpoints;
using Vuelto.Api.Services;

namespace Vuelto.Api.Features.Email;

/// <summary>
/// EMAIL-5/6 routes. <c>/api/merchant-mappings</c>: the household's suggestion rules (list, create, update,
/// delete). <c>/api/pending-vouchers</c>: the review queue (list, count, confirm, discard). Any household
/// member (ADR-V002); the group helper applies the tenant-API policy. Errors are the shared
/// <c>ErrorResponse</c>: 400 <c>invalid_request</c> / <c>exchange_rate_unavailable</c>, 404 <c>not_found</c>
/// (uniform — foreign ids do not exist), 409 <c>mapping_exists</c> / <c>not_pending</c>.
/// </summary>
public static class ReviewEndpoints
{
    public static IEndpointRouteBuilder MapMerchantMappings(this IEndpointRouteBuilder app)
    {
        var group = app.MapTenantFeatureGroup("/api/merchant-mappings");

        group.MapGet("/", async (MerchantMappingHandler handler, CancellationToken ct) => Results.Ok(await handler.ListAsync(ct)));

        group.MapPost("/", async (CreateMerchantMappingRequest request, MerchantMappingHandler handler, CancellationToken ct) =>
        {
            var (mapping, error) = await handler.CreateAsync(request, ct);
            return error is not null ? ToResult(error) : Results.Created($"/api/merchant-mappings/{mapping!.Id}", mapping);
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateMerchantMappingRequest request, MerchantMappingHandler handler, CancellationToken ct) =>
        {
            var (mapping, error) = await handler.UpdateAsync(id, request, ct);
            return error is not null ? ToResult(error) : Results.Ok(mapping);
        });

        group.MapDelete("/{id:guid}", async (Guid id, MerchantMappingHandler handler, CancellationToken ct) =>
            await handler.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound(new ErrorResponse("not_found", "merchant mapping not found")));

        return app;
    }

    public static IEndpointRouteBuilder MapPendingVouchers(this IEndpointRouteBuilder app)
    {
        var group = app.MapTenantFeatureGroup("/api/pending-vouchers");

        group.MapGet("/", async (PendingVoucherHandler handler, CancellationToken ct) => Results.Ok(await handler.ListPendingAsync(ct)));

        group.MapGet("/count", async (PendingVoucherHandler handler, CancellationToken ct) => Results.Ok(new PendingCountResponse(await handler.CountPendingAsync(ct))));

        group.MapPost("/{id:guid}/confirm", async (Guid id, ConfirmVoucherRequest request, PendingVoucherHandler handler, CancellationToken ct) =>
        {
            var (confirmed, error) = await handler.ConfirmAsync(id, request, ct);
            return error is not null ? ToResult(error) : Results.Ok(confirmed);
        });

        group.MapPost("/{id:guid}/discard", async (Guid id, PendingVoucherHandler handler, CancellationToken ct) =>
        {
            var error = await handler.DiscardAsync(id, ct);
            return error is not null ? ToResult(error) : Results.NoContent();
        });

        return app;
    }

    private static IResult ToResult(ErrorResponse error) => error switch
    {
        { Error: "not_found" } => Results.NotFound(error),
        { Error: "invalid_token" } => Results.Json(error, statusCode: StatusCodes.Status401Unauthorized),
        { Error: "mapping_exists" or "not_pending" } => Results.Conflict(error),
        _ => Results.BadRequest(error), // invalid_request, exchange_rate_unavailable
    };
}
