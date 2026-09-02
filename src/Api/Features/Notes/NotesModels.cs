using Perezosoft.Core.Entities;

namespace Perezosoft.Api.Features.Notes;

// 🗑️ DELETE-ME: sample feature slice. See the folder's intent in docs/WAYS_OF_WORKING.md.
// Request/response DTOs live with the feature — not in a shared Models project.

public record CreateNoteRequest(string? Title, string? Content);

public record NoteResponse(Guid Id, string Title, string Content, DateTimeOffset CreatedAt)
{
    public static NoteResponse From(Note n) => new(n.Id, n.Title, n.Content, n.CreatedAt);
}
