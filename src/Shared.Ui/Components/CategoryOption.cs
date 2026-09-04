using System.Text.Json.Serialization;

namespace Vuelto.Shared.Ui.Components;

/// <summary>A category as the <see cref="CategoryPicker"/> lists it — the page deserializes <c>GET /api/categories</c> straight into these.</summary>
public sealed record CategoryOption([property: JsonPropertyName("id")] Guid Id, [property: JsonPropertyName("name")] string Name);
