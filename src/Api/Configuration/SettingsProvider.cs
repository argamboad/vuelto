using Microsoft.Extensions.Configuration;

namespace Perezosoft.Api.Configuration;

/// <summary>
/// Provides typed configuration settings from IConfiguration.
/// Reads and validates configuration once at startup instead of on each request.
/// </summary>
public class JwtSettings : IJwtSettings
{
    public string SecretKey { get; }
    public string Issuer { get; }
    public int ExpiryMinutes { get; }

    public JwtSettings(IConfiguration config)
    {
        SecretKey = config["Jwt:Secret"]
            ?? throw new InvalidOperationException("Jwt:Secret not configured (set Jwt__Secret in .env for dev)");
        Issuer = config["Jwt:Issuer"] ?? "Perezosoft";
        ExpiryMinutes = config.GetValue("Jwt:ExpiryMinutes", 60);

        const int MinSecretKeyLength = 32;
        if (SecretKey.Length < MinSecretKeyLength)
        {
            throw new InvalidOperationException($"Jwt:Secret must be at least {MinSecretKeyLength} characters");
        }
    }
}

public class RefreshTokenSettings : IRefreshTokenSettings
{
    public int ExpiryDays { get; }

    public RefreshTokenSettings(IConfiguration config)
    {
        ExpiryDays = config.GetValue("RefreshToken:ExpiryDays", 30);
    }
}

public class ApplicationSettings : IApplicationSettings
{
    public string ClientUrl { get; }
    public string NativeCallbackScheme { get; }

    public ApplicationSettings(IConfiguration config)
    {
        ClientUrl = config["Auth:AppBaseUrl"]?.TrimEnd('/') ?? "https://localhost:7008";
        NativeCallbackScheme = config["Auth:Native:CallbackScheme"] ?? string.Empty;
    }
}

public class PasswordlessSettings : IPasswordlessSettings
{
    public int MagicLinkLifespanMinutes { get; }
    public int OtpLifespanMinutes { get; }
    public int OtpLength { get; }
    public int OtpMaxAttempts { get; }
    public int OtpLockoutWindowMinutes { get; }

    public PasswordlessSettings(IConfiguration config)
    {
        MagicLinkLifespanMinutes = config.GetValue("Auth:MagicLink:TokenLifespanMinutes", 15);
        OtpLifespanMinutes = config.GetValue("Auth:Otp:CodeLifespanMinutes", 10);
        OtpLength = config.GetValue("Auth:Otp:Length", 6);
        OtpMaxAttempts = config.GetValue("Auth:Otp:MaxAttempts", 5);
        OtpLockoutWindowMinutes = config.GetValue("Auth:Otp:LockoutWindowMinutes", 15);
    }
}

public class MfaSettings : IMfaSettings
{
    public int MaxAttempts { get; }
    public int LockoutWindowMinutes { get; }

    public MfaSettings(IConfiguration config)
    {
        MaxAttempts = config.GetValue("Auth:Mfa:MaxAttempts", 5);
        LockoutWindowMinutes = config.GetValue("Auth:Mfa:LockoutWindowMinutes", 15);
    }
}

public class InvitationSettings : IInvitationSettings
{
    public int LifespanDays { get; }

    public InvitationSettings(IConfiguration config)
    {
        LifespanDays = config.GetValue("Auth:Invitation:LifespanDays", 7);
    }
}
