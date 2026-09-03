using Vuelto.Api.Endpoints;
using Vuelto.Api.Services;

namespace Vuelto.Api.Features.Envelopes;

/// <summary>
/// ENV-1 routes under <c>/api/envelopes</c>: list (<c>include_inactive</c>), create, update. Any
/// household member may read and edit (member baseline, ADR-V002); the group helper applies the
/// tenant-API policy. No DELETE — deactivate instead (ADR-V008).
/// </summary>
public static class EnvelopeEndpoints
{
    private const string Prefix = "/api/envelopes";

    public static IEndpointRouteBuilder MapEnvelopes(this IEndpointRouteBuilder app)
    {
        var group = app.MapTenantFeatureGroup(Prefix);

        group.MapGet("/", async (bool? include_inactive, EnvelopeHandler handler, CancellationToken ct) =>
        {
            var list = await handler.ListAsync(include_inactive ?? false, ct);
            return list is null ? Results.Unauthorized() : Results.Ok(list);
        });

        group.MapPost("/", async (CreateEnvelopeRequest request, EnvelopeHandler handler, CancellationToken ct) =>
        {
            var (envelope, error) = await handler.CreateAsync(request, ct);
            return error is not null ? ToResult(error) : Results.Created($"{Prefix}/{envelope!.Id}", envelope);
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateEnvelopeRequest request, EnvelopeHandler handler, CancellationToken ct) =>
        {
            var (envelope, error) = await handler.UpdateAsync(id, request, ct);
            return error is not null ? ToResult(error) : Results.Ok(envelope);
        });

        return app;
    }

    private static IResult ToResult(ErrorResponse error) => error switch
    {
        EnvelopeConflictResponse conflict => Results.Conflict(conflict),
        { Error: "not_found" } => Results.NotFound(error),
        { Error: "invalid_token" } => Results.Json(error, statusCode: StatusCodes.Status401Unauthorized),
        _ => Results.BadRequest(error),
    };
}
