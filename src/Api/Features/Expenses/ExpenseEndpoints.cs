using Vuelto.Api.Endpoints;
using Vuelto.Api.Services;

namespace Vuelto.Api.Features.Expenses;

/// <summary>
/// EXPENSES-1 routes — the same four endpoints under <c>/api/expenses/fixed</c> and
/// <c>/api/expenses/variable</c>: list (<c>include_inactive</c>), create, update, and
/// <c>PUT …/order</c> with <c>ordered_ids</c>. Any household member (member baseline, ADR-V002); the
/// group helper applies the tenant-API policy. Handlers resolve through the non-generic
/// <see cref="IExpenseLineHandler"/> (the route-handler analyzer cannot take open generics).
/// </summary>
public static class ExpenseEndpoints
{
    public static IEndpointRouteBuilder MapExpenses(this IEndpointRouteBuilder app)
    {
        MapList(app, "/api/expenses/fixed", ctx => ctx.RequestServices.GetRequiredService<FixedExpenseHandler>());
        MapList(app, "/api/expenses/variable", ctx => ctx.RequestServices.GetRequiredService<VariableExpenseHandler>());
        return app;
    }

    private static void MapList(IEndpointRouteBuilder app, string prefix, Func<HttpContext, IExpenseLineHandler> resolve)
    {
        var group = app.MapTenantFeatureGroup(prefix);

        group.MapGet("/", async (bool? include_inactive, HttpContext ctx, CancellationToken ct) =>
        {
            var list = await resolve(ctx).ListAsync(include_inactive ?? false, ct);
            return list is null ? Results.Unauthorized() : Results.Ok(list);
        });

        group.MapPost("/", async (CreateExpenseRequest request, HttpContext ctx, CancellationToken ct) =>
        {
            var (line, error) = await resolve(ctx).CreateAsync(request, ct);
            return error is not null ? ToResult(error) : Results.Created($"{prefix}/{line!.Id}", line);
        });

        group.MapPut("/order", async (ReorderExpenseRequest request, HttpContext ctx, CancellationToken ct) =>
        {
            var error = await resolve(ctx).ReorderAsync(request, ct);
            return error is not null ? ToResult(error) : Results.NoContent();
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateExpenseRequest request, HttpContext ctx, CancellationToken ct) =>
        {
            var (line, error) = await resolve(ctx).UpdateAsync(id, request, ct);
            return error is not null ? ToResult(error) : Results.Ok(line);
        });
    }

    private static IResult ToResult(ErrorResponse error) => error switch
    {
        ExpenseConflictResponse conflict => Results.Conflict(conflict),
        { Error: "not_found" } => Results.NotFound(error),
        { Error: "invalid_token" } => Results.Json(error, statusCode: StatusCodes.Status401Unauthorized),
        _ => Results.BadRequest(error),
    };
}
