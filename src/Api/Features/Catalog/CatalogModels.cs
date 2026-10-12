using System.Text.Json.Serialization;
using Vuelto.Api.Services;
using Vuelto.Core.Budget;

namespace Vuelto.Api.Features.Catalog;

// CATALOG-1/2 DTOs — one shape for categories and banks (ADR-V008). Wire format: snake_case.

public record CreateCatalogEntryRequest([property: JsonPropertyName("name")] string? Name);

public record UpdateCatalogEntryRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("is_active")] bool IsActive);

public record CatalogEntryResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("is_active")] bool IsActive)
{
    public static CatalogEntryResponse From(ICatalogEntry e) => new(e.Id, e.Name, e.IsActive);
}

/// <summary>
/// The 409 body for a name clash — the shared <see cref="ErrorResponse"/> shape plus, for an
/// <c>*_exists_inactive</c> clash, the existing entry's id and stored name so the client can offer
/// one-click reactivation (a PUT with that name and <c>is_active: true</c>) — restoring the entry
/// as it was, not renaming it to whatever casing the user just typed.
/// </summary>
public record CatalogConflictResponse(
    string Error,
    string Message,
    [property: JsonPropertyName("existing_id")] Guid? ExistingId,
    [property: JsonPropertyName("existing_name")] string? ExistingName) : ErrorResponse(Error, Message);
