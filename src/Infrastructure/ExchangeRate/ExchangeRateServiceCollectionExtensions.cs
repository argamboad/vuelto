using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Core.Budget;

namespace Vuelto.Infrastructure.ExchangeRate;

/// <summary>App extension (never a platform edit): registers the exchange-rate provider for FX-1.</summary>
public static class ExchangeRateServiceCollectionExtensions
{
    public static IServiceCollection AddExchangeRates(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ExchangeRateSettings>(configuration.GetSection("ExchangeRate"));
        // Typed client (transient) over a singleton IMemoryCache, so the freshness window spans requests.
        // ADR-V019: BCCR's buy/sell pair by default; the world feed stays selectable (ExchangeRate:Provider).
        var provider = configuration["ExchangeRate:Provider"]?.Trim().ToLowerInvariant();
        if (provider == "exchangerate-api")
            services.AddHttpClient<IExchangeRateService, ExchangeRateApiClient>(c => c.Timeout = TimeSpan.FromSeconds(10));
        else
            services.AddHttpClient<IExchangeRateService, BccrExchangeRateClient>(c => c.Timeout = TimeSpan.FromSeconds(10));
        return services;
    }
}
