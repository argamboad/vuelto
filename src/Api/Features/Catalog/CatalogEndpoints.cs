using System.Security.Claims;
using Vuelto.Api.Endpoints;
using Vuelto.Api.Services;

namespace Vuelto.Api.Features.Catalog;

/// <summary>
/// CATALOG-1/2 routes — the same three endpoints under <c>/api/categories</c> and <c>/api/banks</c>.
/// Any household member may read and edit (member baseline, ADR-V002). The seeding locale comes from
/// the JWT's <c>locale</c> claim (the platform's per-user preference), so a household's first reader
/// decides the language of its defaults (ADR-V009).
/// <para>
/// The handlers are resolved through <see cref="ICatalogHandler"/> rather than a generic type
/// parameter: the ASP.NET route-handler analyzer cannot analyse lambdas whose parameters are open
/// generics (it crashes under warnings-as-errors), and the two groups are otherwise identical.
/// </para>
/// </summary>
public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalog(this IEndpointRouteBuilder app)
    {
        MapCatalogGroup(app, "/api/categories", ctx => ctx.RequestServices.GetRequiredService<CategoryCatalogHandler>());
        MapCatalogGroup(app, "/api/banks", ctx => ctx.RequestServices.GetRequiredService<BankCatalogHandler>());
        return app;
    }

    private static void MapCatalogGroup(IEndpointRouteBuilder app, string prefix, Func<HttpContext, ICatalogHandler> resolve)
    {
        var group = app.MapTenantFeatureGroup(prefix);

        group.MapGet("/", async (bool? include_inactive, ClaimsPrincipal user, HttpContext ctx, CancellationToken ct) =>
        {
            var list = await resolve(ctx).ListAsync(include_inactive ?? false, user.FindFirstValue(JwtClaims.Locale), ct);
            return list is null ? Results.Unauthorized() : Results.Ok(list);
        });

        group.MapPost("/", async (CreateCatalogEntryRequest request, HttpContext ctx, CancellationToken ct) =>
        {
            var (entry, error) = await resolve(ctx).CreateAsync(request, ct);
            return error is not null ? ToResult(error) : Results.Created($"{prefix}/{entry!.Id}", entry);
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateCatalogEntryRequest request, HttpContext ctx, CancellationToken ct) =>
        {
            var (entry, error) = await resolve(ctx).UpdateAsync(id, request, ct);
            return error is not null ? ToResult(error) : Results.Ok(entry);
        });
    }

    private static IResult ToResult(ErrorResponse error) => error switch
    {
        CatalogConflictResponse conflict => Results.Conflict(conflict),
        { Error: "not_found" } => Results.NotFound(error),
        { Error: "invalid_token" } => Results.Json(error, statusCode: StatusCodes.Status401Unauthorized),
        _ => Results.BadRequest(error),
    };
}
