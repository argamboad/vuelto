namespace Vuelto.Core.Budget;

/// <summary>
/// The dual-currency derivation (ADR-V004): both amounts are computed from the original amount, its
/// currency and a rate (colones per dollar) — never entered. Fixed-point, 2 dp, half away from zero.
/// </summary>
public static class CurrencyMath
{
    public static decimal Round2(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Derives (amount_crc, amount_usd) from an original amount in <paramref name="currency"/> at <paramref name="rate"/>.</summary>
    /// <exception cref="ExchangeRateUnavailableException">A non-positive rate can never derive an amount (donor US-034).</exception>
    public static (decimal AmountCrc, decimal AmountUsd) DeriveAmounts(decimal originalAmount, string currency, decimal rate)
    {
        if (rate <= 0)
            throw new ExchangeRateUnavailableException($"Cannot derive amounts: the exchange rate must be positive (got {rate})");

        return Currencies.Normalize(currency) switch
        {
            Currencies.Crc => (Round2(originalAmount), Round2(originalAmount / rate)),
            Currencies.Usd => (Round2(originalAmount * rate), Round2(originalAmount)),
            _ => throw new ArgumentException($"'{currency}' is not a supported currency", nameof(currency)),
        };
    }
}
