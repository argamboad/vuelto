using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Vuelto.Api.Configuration;

/// <summary>
/// Registers the typed configuration settings (the IConfiguration-ctor pattern — ADR-001, DEBT-1)
/// in one place. Each settings object reads and validates its config section once at construction,
/// and is held as a singleton, so a missing/invalid value fails fast at startup.
/// </summary>
public static class SettingsRegistration
{
    /// <summary>
    /// Registers all five typed settings singletons. <see cref="IJwtSettings"/> is built into a local
    /// so the caller can reuse the SAME instance for JWT-bearer validation (no duplicate construction).
    /// </summary>
    public static IJwtSettings AddAppSettings(this IServiceCollection services, IConfiguration config)
    {
        // Build JwtSettings once; the same instance backs both the DI singleton and the JWT-bearer
        // TokenValidationParameters (see Program.cs) — a single source of truth (DEBT-1).
        var jwtSettings = new JwtSettings(config);

        services.AddSingleton<IJwtSettings>(jwtSettings);
        services.AddSingleton<IRefreshTokenSettings>(new RefreshTokenSettings(config));
        services.AddSingleton<IApplicationSettings>(new ApplicationSettings(config));
        services.AddSingleton<IPasswordlessSettings>(new PasswordlessSettings(config));
        services.AddSingleton<IMfaSettings>(new MfaSettings(config));
        services.AddSingleton<IInvitationSettings>(new InvitationSettings(config));

        return jwtSettings;
    }
}
