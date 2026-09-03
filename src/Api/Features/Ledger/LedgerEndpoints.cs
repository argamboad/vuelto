using Vuelto.Api.Endpoints;
using Vuelto.Api.Services;

namespace Vuelto.Api.Features.Ledger;

/// <summary>
/// LEDGER-1/2 routes. <c>/api/months</c>: list, resolve a date, read one (with weeks), edit income,
/// list its transactions. <c>/api/transactions</c>: create, read, update, delete. Any household member
/// (member baseline, ADR-V002); the group helper applies the tenant-API policy. Errors are the shared
/// <c>ErrorResponse</c>: 400 <c>invalid_request</c> / <c>exchange_rate_unavailable</c> /
/// <c>derived_transaction</c>, 404 <c>not_found</c> (uniform — foreign ids do not exist).
/// </summary>
public static class LedgerEndpoints
{
    public static IEndpointRouteBuilder MapLedger(this IEndpointRouteBuilder app)
    {
        var months = app.MapTenantFeatureGroup("/api/months");

        months.MapGet("/", async (MonthHandler handler, CancellationToken ct) =>
        {
            var list = await handler.ListAsync(ct);
            return list is null ? Results.Unauthorized() : Results.Ok(list);
        });

        months.MapGet("/resolve", async (DateOnly? date, MonthHandler handler, CancellationToken ct) =>
        {
            if (date is not { } d) return Results.BadRequest(new ErrorResponse("invalid_request", "date is required (yyyy-MM-dd)"));
            var resolved = await handler.ResolveAsync(d, ct);
            return resolved is null ? Results.Unauthorized() : Results.Ok(resolved);
        });

        months.MapGet("/{id:guid}", async (Guid id, MonthHandler handler, CancellationToken ct) =>
        {
            var month = await handler.GetAsync(id, ct);
            return month is null ? Results.NotFound(new ErrorResponse("not_found", "month not found")) : Results.Ok(month);
        });

        months.MapPut("/{id:guid}/income", async (Guid id, UpdateMonthIncomeRequest request, MonthHandler handler, CancellationToken ct) =>
        {
            var (month, error) = await handler.UpdateIncomeAsync(id, request, ct);
            return error is not null ? ToResult(error) : Results.Ok(month);
        });

        months.MapGet("/{id:guid}/transactions", async (Guid id, TransactionHandler handler, CancellationToken ct) =>
        {
            var list = await handler.ListForMonthAsync(id, ct);
            return list is null ? Results.NotFound(new ErrorResponse("not_found", "month not found")) : Results.Ok(list);
        });

        var transactions = app.MapTenantFeatureGroup("/api/transactions");

        transactions.MapPost("/", async (CreateTransactionRequest request, TransactionHandler handler, CancellationToken ct) =>
        {
            var (tx, error) = await handler.CreateAsync(request, ct);
            return error is not null ? ToResult(error) : Results.Created($"/api/transactions/{tx!.Id}", tx);
        });

        transactions.MapGet("/{id:guid}", async (Guid id, TransactionHandler handler, CancellationToken ct) =>
        {
            var tx = await handler.GetAsync(id, ct);
            return tx is null ? Results.NotFound(new ErrorResponse("not_found", "transaction not found")) : Results.Ok(tx);
        });

        transactions.MapPut("/{id:guid}", async (Guid id, UpdateTransactionRequest request, TransactionHandler handler, CancellationToken ct) =>
        {
            var (tx, error) = await handler.UpdateAsync(id, request, ct);
            return error is not null ? ToResult(error) : Results.Ok(tx);
        });

        transactions.MapDelete("/{id:guid}", async (Guid id, TransactionHandler handler, CancellationToken ct) =>
        {
            var error = await handler.DeleteAsync(id, ct);
            return error is not null ? ToResult(error) : Results.NoContent();
        });

        return app;
    }

    private static IResult ToResult(ErrorResponse error) => error switch
    {
        { Error: "not_found" } => Results.NotFound(error),
        { Error: "invalid_token" } => Results.Json(error, statusCode: StatusCodes.Status401Unauthorized),
        _ => Results.BadRequest(error), // invalid_request, exchange_rate_unavailable, derived_transaction
    };
}
