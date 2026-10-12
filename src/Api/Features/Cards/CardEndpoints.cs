using Vuelto.Api.Endpoints;
using Vuelto.Api.Services;

namespace Vuelto.Api.Features.Cards;

/// <summary>CARDS-1 routes under <c>/api/cards</c>: list (optionally inactive), create, update. Any household member may read and edit (ADR-V002).</summary>
public static class CardEndpoints
{
    public static IEndpointRouteBuilder MapCards(this IEndpointRouteBuilder app)
    {
        var group = app.MapTenantFeatureGroup("/api/cards");

        group.MapGet("/", async (bool? include_inactive, CardHandler handler, CancellationToken ct) =>
        {
            var list = await handler.ListAsync(include_inactive ?? false, ct);
            return list is null ? Results.Unauthorized() : Results.Ok(list);
        });

        group.MapPost("/", async (CreateCardRequest request, CardHandler handler, CancellationToken ct) =>
        {
            var (card, error) = await handler.CreateAsync(request, ct);
            return error is not null ? ToResult(error) : Results.Created($"/api/cards/{card!.Id}", card);
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateCardRequest request, CardHandler handler, CancellationToken ct) =>
        {
            var (card, error) = await handler.UpdateAsync(id, request, ct);
            return error is not null ? ToResult(error) : Results.Ok(card);
        });

        // POST /{id}/merge { into } — a renewed card is the same card: identities + transactions move under `into`, the duplicate goes.
        group.MapPost("/{id:guid}/merge", async (Guid id, MergeCardRequest request, CardHandler handler, CancellationToken ct) =>
        {
            if (request.Into is not { } into) return Results.BadRequest(new ErrorResponse("invalid_request", "into is required"));
            var (card, error) = await handler.MergeAsync(id, into, ct);
            return error is not null ? ToResult(error) : Results.Ok(card);
        });

        return app;
    }

    private static IResult ToResult(ErrorResponse error) => error switch
    {
        CardConflictResponse conflict => Results.Conflict(conflict),
        { Error: "not_found" } => Results.NotFound(error),
        { Error: "invalid_token" } => Results.Json(error, statusCode: StatusCodes.Status401Unauthorized),
        _ => Results.BadRequest(error),
    };
}
