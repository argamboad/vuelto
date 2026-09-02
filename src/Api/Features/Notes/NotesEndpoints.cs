using Perezosoft.Api.Endpoints;
using Perezosoft.Api.Services;

namespace Perezosoft.Api.Features.Notes;

/// <summary>
/// 🗑️ DELETE-ME: sample feature endpoints. A vertical slice registers its own routes via
/// <c>MapTenantFeatureGroup</c> (call <c>app.MapNotes()</c> in <c>Program.cs</c>) instead of a
/// controller — features are minimal-API groups, the platform stays controllers. The helper
/// applies the shared tenant-API auth policy, so the slice never re-spells (or forgets) authz.
/// </summary>
public static class NotesEndpoints
{
    public static IEndpointRouteBuilder MapNotes(this IEndpointRouteBuilder app)
    {
        var group = app.MapTenantFeatureGroup("/api/notes");

        group.MapGet("/", async (NotesHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.ListAsync(ct)));

        group.MapPost("/", async (CreateNoteRequest request, NotesHandler handler, CancellationToken ct) =>
        {
            var created = await handler.CreateAsync(request, ct);
            // Errors use the SHARED ErrorResponse shape (v3 audit TR-5) — this exemplar is what every
            // downstream slice copies, and ad-hoc anonymous error shapes are banned in Features/**.
            return created is null
                ? Results.BadRequest(new ErrorResponse("invalid_request", "A note title is required"))
                : Results.Created($"/api/notes/{created.Id}", created);
        });

        group.MapDelete("/{id:guid}", async (Guid id, NotesHandler handler, CancellationToken ct) =>
            await handler.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        return app;
    }
}
