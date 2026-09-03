using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vuelto.Core.Mail;

namespace Vuelto.Infrastructure.Mail;

/// <summary>
/// App extension (never a platform edit): EMAIL-2/3 mail ingestion — token protection on the platform
/// key ring, the consent service on the platform's own <c>Authentication:*</c> OAuth apps, and the two
/// provider readers behind <see cref="IEmailReader"/>.
/// </summary>
public static class MailServiceCollectionExtensions
{
    public static MailConsentSettings BuildConsentSettings(IConfiguration configuration) => new()
    {
        MicrosoftClientId = configuration["Authentication:Microsoft:ClientId"] ?? "",
        MicrosoftClientSecret = configuration["Authentication:Microsoft:ClientSecret"] ?? "",
        MicrosoftTenant = configuration["Authentication:Microsoft:Tenant"] ?? "consumers", // mirrors the login handler's default
        GoogleClientId = configuration["Authentication:Google:ClientId"] ?? "",
        GoogleClientSecret = configuration["Authentication:Google:ClientSecret"] ?? "",
    };

    public static IServiceCollection AddMailIngestion(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(BuildConsentSettings(configuration));
        services.AddSingleton<IEmailTokenProtector, DataProtectionEmailTokenProtector>();
        services.AddHttpClient<IMailConsentService, MailConsentService>(c => c.Timeout = TimeSpan.FromSeconds(20));
        services.AddHttpClient<GraphEmailReader>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<GmailEmailReader>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddTransient<IEmailReader>(sp => sp.GetRequiredService<GraphEmailReader>());
        services.AddTransient<IEmailReader>(sp => sp.GetRequiredService<GmailEmailReader>());
        return services;
    }
}
