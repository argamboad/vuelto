using System.Text.Json.Serialization;

namespace Vuelto.Api.Features.DisplaySettings;

/// <summary><c>is_default</c> = the user never chose (the client may then adopt the device's choice, like the theme — ADR-V020).</summary>
public record DisplaySettingsResponse(
    [property: JsonPropertyName("display_currency")] string DisplayCurrency,
    [property: JsonPropertyName("is_default")] bool IsDefault);

public record UpdateDisplaySettingsRequest([property: JsonPropertyName("display_currency")] string? DisplayCurrency);
