using Microsoft.Extensions.DependencyInjection;
using Vuelto.Core.Vouchers;

namespace Vuelto.Infrastructure.Vouchers;

/// <summary>EMAIL-1: the pure voucher-parsing library — the built-in BAC/BN extractors, the default routing map and the validating facade.</summary>
public static class VoucherServiceCollectionExtensions
{
    public static IServiceCollection AddVoucherParsing(this IServiceCollection services)
    {
        services.AddSingleton<IBankVoucherExtractor, BacVoucherExtractor>();
        services.AddSingleton<IBankVoucherExtractor, BnVoucherExtractor>();
        services.AddSingleton<IBankVoucherExtractor, BnPaymentExtractor>();
        services.AddSingleton(BankVoucherMap.Default);
        services.AddSingleton<IVoucherParser, VoucherParser>();
        return services;
    }
}
