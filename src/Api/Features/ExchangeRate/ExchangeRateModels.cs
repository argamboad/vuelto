using System.Text.Json.Serialization;
using Vuelto.Core.Budget;

namespace Vuelto.Api.Features.ExchangeRate;

/// <summary>FX-1 read shape (snake_case, ADR-V012): the resolved USD→CRC rate and where it came from.</summary>
public record ExchangeRateResponse(
    [property: JsonPropertyName("rate")] decimal Rate,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("as_of")] DateTimeOffset AsOf)
{
    public static ExchangeRateResponse From(ResolvedRate r) => new(r.Rate, r.Source, r.AsOf);
}
