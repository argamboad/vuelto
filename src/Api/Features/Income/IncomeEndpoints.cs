using Vuelto.Api.Endpoints;
using Vuelto.Api.Services;

namespace Vuelto.Api.Features.Income;

/// <summary>
/// INCOME-1 routes under <c>/api/incomes</c>: list (<c>include_inactive</c>), create, update, and <c>PUT /order</c>
/// with <c>ordered_ids</c>. Any household member; the group helper applies the tenant-API policy. 409
/// <c>income_exists</c> / <c>income_exists_inactive</c> (reactivation offer), 404 for another household's id.
/// </summary>
public static class IncomeEndpoints
{
    public static IEndpointRouteBuilder MapIncomes(this IEndpointRouteBuilder app)
    {
        var group = app.MapTenantFeatureGroup("/api/incomes");

        group.MapGet("/", async (bool? include_inactive, IncomeHandler handler, CancellationToken ct) =>
        {
            var list = await handler.ListAsync(include_inactive ?? false, ct);
            return list is null ? Results.Unauthorized() : Results.Ok(list);
        });

        group.MapPost("/", async (IncomeLineRequest request, IncomeHandler handler, CancellationToken ct) =>
        {
            var (line, error) = await handler.CreateAsync(request, ct);
            return error is not null ? ToResult(error) : Results.Created($"/api/incomes/{line!.Id}", line);
        });

        group.MapPut("/order", async (ReorderIncomeRequest request, IncomeHandler handler, CancellationToken ct) =>
        {
            var error = await handler.ReorderAsync(request, ct);
            return error is not null ? ToResult(error) : Results.NoContent();
        });

        group.MapPut("/{id:guid}", async (Guid id, IncomeLineRequest request, IncomeHandler handler, CancellationToken ct) =>
        {
            var (line, error) = await handler.UpdateAsync(id, request, ct);
            return error is not null ? ToResult(error) : Results.Ok(line);
        });

        return app;
    }

    private static IResult ToResult(ErrorResponse error) => error switch
    {
        IncomeConflictResponse conflict => Results.Conflict(conflict),
        { Error: "not_found" } => Results.NotFound(error),
        { Error: "invalid_token" } => Results.Json(error, statusCode: StatusCodes.Status401Unauthorized),
        _ => Results.BadRequest(error),
    };
}
