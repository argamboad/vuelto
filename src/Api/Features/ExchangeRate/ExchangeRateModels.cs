using System.Text.Json.Serialization;
using Vuelto.Core.Budget;

namespace Vuelto.Api.Features.ExchangeRate;

/// <summary>
/// FX-1 read shape (snake_case, ADR-V012): the resolved USD→CRC pair and where it came from. <c>rate</c> is the
/// sell side (what a dollar costs — the "per $1" figure, kept for older clients); <c>buy</c>/<c>sell</c> are the
/// BCCR pair (ADR-V019; equal when the source carries one rate).
/// </summary>
public record ExchangeRateResponse(
    [property: JsonPropertyName("rate")] decimal Rate,
    [property: JsonPropertyName("buy")] decimal Buy,
    [property: JsonPropertyName("sell")] decimal Sell,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("as_of")] DateTimeOffset AsOf)
{
    public static ExchangeRateResponse From(ResolvedRate r) => new(r.Rate, r.Rates.Buy, r.Rates.Sell, r.Source, r.AsOf);
}
